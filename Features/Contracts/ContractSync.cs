using System;
using System.Collections.Generic;
using System.Linq;
using StoneForge;
using StoneshardMP.Features.World;
using StoneshardMP.Features.Join;
using StoneshardMP.Features.Quests;
using StoneshardMP.Net;
using StoneshardMP.Net.Packets;

// The game scripts contracts are made and counted through (the patcher makes them hookable).
[assembly: HookScript(nameof(Scripts.scr_contract_add))]
[assembly: HookScript(nameof(Scripts.scr_globaltile_set))]
[assembly: HookScript(nameof(Scripts.scr_contract_delete))]

namespace StoneshardMP.Features.Contracts;

// One set of contracts for everyone in a world (legacy StoneshardMP's contract sync):
// - The host's: contracts are made at random over time in every game, so a client in the host's world makes none of
//   its own (scr_contract_add skipped) and has the host's.
// - Compared, not hooked: contracts change in many places (dialogue, kills, items, the clock), so twice a second every
//   contract - every kind, and every one handed out - is compared with what was last sent, and the changed ones go to
//   the others (ContractPacket), who copy them into theirs in place: the game's references to a contract (journal,
//   diary, its dungeon) see the change, and a taken one goes into their journal (ContractData.Apply). A client just in
//   the host's world gets a full copy.
// - The host's clock: taken contracts' deadlines count down hourly, which drifted apart counted in every game (a
//   clock jump, or standing on the dungeon's tile, stops one game's count but not another's). While playing together
//   the host counts them (ContractData.ClockHour: paused while any player is at the contract's dungeon) and clients
//   don't; a failure reaches them with the contract.
// - A village's contract counts (out, completed) are shared calls (QuestSync), and a dungeon's contract values come
//   with the dungeon (WorldSync): a contract's index is the same in every game.
// - The offers on a village's board are the host's too. Every game's day (o_time_controller's user event 6) runs their
//   expiry down and, at random, gives an offer away to someone else (deleted): a client's day did it its own way and
//   sent the result, so the host's board lost offers to a client's dice, and its count with them (#76: two offers, and
//   none after a night). A client's day deletes none (they go when the host's do), and the host doesn't take a
//   client's change to an offer neither of them has taken.
public sealed class ContractSync
{
    // (Twice a second.)
    private const int Interval = 30;
    private static readonly HashSet<string> VillageCounts = new() { "Contract_Current_Count", "contract_complete_count" };

    private readonly ModContext _context;
    private readonly Session _session;
    private readonly JoinManager _join;
    // What each contract ("list:index") was when last sent or taken.
    private readonly Dictionary<string, string> _sent = new();
    private bool _wasSharing, _wasInWorld;
    // Inside the game's daily contract upkeep (o_time_controller's user event 6).
    private int _day;
    private int _frame;

    public ContractSync(ModContext context, Session session, JoinManager join, QuestSync quests)
    {
        _context = context;
        _session = session;
        _join = join;
        session.On<ContractPacket>(Receive);
        // A client in the host's world makes no contracts of its own.
        Scripts.scr_contract_add.Before(context, call =>
        {
            if (_session.Mode != Session.SessionMode.Client || !_join.ClientInWorld)
                return false;
            call.Result = call.Args.Length > 0 ? call.Args[0] : GmValue.Undefined;
            return true;
        });
        // The day's upkeep: a client deletes no offers of its own.
        context.OnCode("gml_Object_o_time_controller_Other_16",
            before: (_, _) => { _day++; return false; },
            after: (_, _) => _day = Math.Max(0, _day - 1));
        Scripts.scr_contract_delete.Before(context, call =>
            _day > 0 && _session.Mode == Session.SessionMode.Client && _join.ClientInWorld);
        // The hourly deadline count: the host's alone while playing together.
        context.OnCode("gml_Object_o_time_controller_Other_13", before: (_, _) => ClockHour());
        // A village's contract counts (scr_globaltile_set: key, value, x, y, layer): the tile is filled in where the
        // caller stands; the world map's own layer only.
        quests.Share(Scripts.scr_globaltile_set, args =>
        {
            if (args.Length < 2 || !VillageCounts.Contains(args[0].AsString) || (args.Length > 4 && !args[4].IsUndefined && args[4].AsReal != 0))
                return null;
            if (WorldMap.PlayerCell is not { } here)
                return null;
            var sent = new GmValue[4];
            Array.Copy(args, sent, Math.Min(args.Length, 4));
            if (sent[2].IsUndefined)
                sent[2] = here.X;
            if (sent[3].IsUndefined)
                sent[3] = here.Y;
            return sent;
        });
    }

    public void Clear()
    {
        _sent.Clear();
        _wasSharing = _wasInWorld = false;
    }

    // Whether we share contracts: the host in a world with players, a client in the host's.
    private bool Sharing => _session.Mode switch
    {
        Session.SessionMode.Host => JoinSave.HostInWorld() && _session.Players.Any(),
        Session.SessionMode.Client => _join.ClientInWorld,
        _ => false,
    };

    // Each frame.
    public void Tick()
    {
        bool sharing = Sharing;
        // A client just in the host's world: asks for the host's full copy.
        bool inWorld = _session.Mode == Session.SessionMode.Client && _join.ClientInWorld;
        if (inWorld && !_wasInWorld)
            _session.Send(new ContractPacket(ContractKind.CopyRequest, SharedWorld.WorldSeed(), 0, 0, Array.Empty<byte>()), to: 0);
        _wasInWorld = inWorld;
        if (!sharing)
        {
            _wasSharing = false;
            _sent.Clear();
            return;
        }
        // (The first look only notes what we have: a client takes the host's full copy, and the host sends one.)
        bool first = !_wasSharing;
        _wasSharing = true;
        if (!first && ++_frame % Interval != 0)
            return;
        for (byte list = 0; list < 2; list++)
        {
            if (ContractData.Export(list) is not { } contracts)
                continue;
            for (int index = 0; index < contracts.Count; index++)
            {
                string json = contracts[index];
                string key = $"{list}:{index}";
                if (json.Length > 0 && (!_sent.TryGetValue(key, out string? was) || was != json))
                {
                    _sent[key] = json;
                    if (!first)
                        Send(ContractKind.Changed, list, index, json, Session.Everyone);
                }
            }
        }
    }

    private void Send(ContractKind kind, byte list, int index, string json, int to)
        => _session.Send(new ContractPacket(kind, SharedWorld.WorldSeed(), list, index, JoinCompression.Compress(json)), to);

    // Host: every contract, as a full copy, to a client just in our world.
    private void SendAll(int to)
    {
        int sent = 0;
        for (byte list = 0; list < 2; list++)
        {
            if (ContractData.Export(list) is not { } contracts)
                continue;
            for (int index = 0; index < contracts.Count; index++)
            {
                string json = contracts[index];
                if (json.Length > 0)
                {
                    _sent[$"{list}:{index}"] = json;
                    Send(ContractKind.Full, list, index, json, to);
                    sent++;
                }
            }
        }
        string name = _session.Players.FirstOrDefault(p => p.Slot == to)?.Name ?? "a player";
        _context.Log($"Sent our {sent} contracts to {name}");
    }

    private void Receive(RemotePlayer sender, ContractPacket packet)
    {
        // Only from and for a game in our world.
        if (!Sharing || packet.Seed != SharedWorld.WorldSeed())
            return;
        if (packet.Kind == ContractKind.CopyRequest)
        {
            if (_session.Mode == Session.SessionMode.Host)
                SendAll(sender.Slot);
            return;
        }
        // (A client's full copy is the host's alone.)
        if (packet.Kind == ContractKind.Full && (_session.Mode != Session.SessionMode.Client || sender.Slot != 0))
            return;
        string json = JoinCompression.Decompress(packet.Data);
        // (An offer on a board neither of us has taken: the host's to change.)
        if (_session.Mode == Session.SessionMode.Host && packet.List == 1 && !ContractData.Taken(1, packet.Index)
            && !ContractData.TakenIn(json))
            return;
        string now = ContractData.Apply(packet.List, packet.Index, json,
            packet.Kind == ContractKind.Full, _session.Mode == Session.SessionMode.Host);
        // What we have now counts as sent: not sent back.
        if (now.Length > 0)
            _sent[$"{packet.List}:{packet.Index}"] = now;
    }

    // o_time_controller's hourly deadline count: skipped on a client in the host's world (the host's comes with its
    // contracts); counted by the host itself while playing together. Returns whether the game's own is skipped.
    private bool ClockHour()
    {
        if (_session.Mode == Session.SessionMode.Client)
            return _join.ClientInWorld;
        if (!Sharing)
            return false;
        // The world-map cells the other players are on ("@x_y" ends their place).
        string occupied = string.Concat(_session.Players
            .Select(p => p.State?.Place)
            .OfType<string>()
            .Where(place => place.Contains('@'))
            .Select(place => place[(place.LastIndexOf('@') + 1)..] + ","));
        string line = ContractData.ClockHour(occupied);
        if (line.Length > 0)
            _context.Log("Contract clock:" + line);
        return true;
    }
}
