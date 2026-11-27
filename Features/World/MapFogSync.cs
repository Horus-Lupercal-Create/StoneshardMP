using System;
using System.Collections.Generic;
using System.Linq;
using StoneForge;
using StoneshardMP.Net;

// A world-map tile uncovered (scr_globalmapFogUpdate: the tile entered, and the fog around it).
[assembly: HookScript(nameof(Scripts.scr_globalmapFogUpdate))]
// Tiles put on the paper map (scr_globalmapPaperUpdateArray: a map read, a region, the tiles walked - every way comes here).
[assembly: HookScript(nameof(Scripts.scr_globalmapPaperUpdateArray))]

namespace StoneshardMP.Features.World;

// The world map's explored land is everyone's: a tile one player uncovers (scr_globalmapFogUpdate - entering it) is
// uncovered in the others' games too, by the game's own call there (the fog around it, mountains, the caravan's paths,
// points of interest - no discovery XP: that's scr_glmap_openLocation's, shared on its own as the tile's isOpen). And
// whenever someone comes into the world, each game sends all it has uncovered, so what each player explored before
// they played together is put together too (#46).
// The paper map the same way (global.globalmapPaperGrid: what the world map draws as charted - a map read, a region a
// quest shows, the land walked): a client reading a map charted it for itself only, and the host had to read one to
// share it, then load again (#77, #62).
public sealed class MapFogSync
{
    // (Who's in the world looked at this often - and what we've just uncovered sent at most this often.)
    private const int Interval = 30;
    private readonly ModContext _context;
    private readonly Session _session;
    private readonly Func<bool> _inSharedWorld;
    private readonly List<ushort> _uncovered = new();
    private readonly List<ushort> _charted = new();
    // (Another's paper map that came before ours was made: put on it once it is.)
    private readonly List<ushort> _pendingPaper = new();
    private RemotePlayer? _pendingFrom;
    private string _present = "";
    private int _frame;
    // (Uncovering what another sent: not sent back.)
    private bool _applying;

    public MapFogSync(ModContext context, Session session, Func<bool> inSharedWorld)
    {
        _context = context;
        _session = session;
        _inSharedWorld = inSharedWorld;
        session.On<MapFogPacket>(Receive);
        Scripts.scr_globalmapFogUpdate.Before(context, call =>
        {
            if (!_applying && Ready && call.Args.Length >= 2 && call.Args[0].Kind == GmKind.Real
                && call.Args[1].Kind == GmKind.Real && Fog(call.Args[0].AsInt, call.Args[1].AsInt) == 0)
            {
                _uncovered.Add((ushort)call.Args[0].AsInt);
                _uncovered.Add((ushort)call.Args[1].AsInt);
            }
            return false;
        });
        Scripts.scr_globalmapPaperUpdateArray.Before(context, call =>
        {
            if (_applying || !Ready || call.Args.Length < 1 || call.Args[0].AsArray is not { } tiles)
                return false;
            using (tiles)
                for (int i = 0; i + 1 < tiles.Length; i += 2)
                    if (tiles[i].Kind == GmKind.Real && tiles[i + 1].Kind == GmKind.Real
                        && Paper(tiles[i].AsInt, tiles[i + 1].AsInt) == 0)
                    {
                        _charted.Add((ushort)tiles[i].AsInt);
                        _charted.Add((ushort)tiles[i + 1].AsInt);
                    }
            return false;
        });
    }

    private bool Ready => _session.Connected && _inSharedWorld() && Gm.InGame && Gm.InstanceExists(GameObjectId.o_player);

    public void Clear()
    {
        _uncovered.Clear();
        _charted.Clear();
        _pendingPaper.Clear();
        _pendingFrom = null;
        _present = "";
    }

    public void Tick()
    {
        if (++_frame % Interval != 0)
            return;
        if (!Ready)
        {
            _uncovered.Clear();
            _charted.Clear();
            _pendingPaper.Clear();
            return;
        }
        if (_pendingPaper.Count > 0 && _pendingFrom != null && PaperReady())
        {
            ushort[] pending = _pendingPaper.ToArray();
            _pendingPaper.Clear();
            Chart(_pendingFrom, pending);
        }
        // (Someone's come into the world: all of ours, for them.)
        string present = string.Join(",", _session.Players.Where(p => p.State != null).Select(p => p.Slot).OrderBy(s => s));
        if (present != _present)
        {
            _present = present;
            _uncovered.Clear();
            _charted.Clear();
            if (present.Length > 0 && All("scr_globalmapFogSave", "globalmapFogString") is { Length: > 0 } all)
                _session.Send(new MapFogPacket(all));
            if (present.Length > 0 && All("scr_globalmapPaperSave", "globalmapPaperString") is { Length: > 0 } paper)
                _session.Send(new MapFogPacket(paper, Paper: true));
            return;
        }
        if (_uncovered.Count > 0)
        {
            if (present.Length > 0)
                _session.Send(new MapFogPacket(_uncovered.ToArray()));
            _uncovered.Clear();
        }
        if (_charted.Count > 0)
        {
            if (present.Length > 0)
                _session.Send(new MapFogPacket(_charted.ToArray(), Paper: true));
            _charted.Clear();
        }
    }

    private void Receive(RemotePlayer from, MapFogPacket packet)
    {
        if (!Ready)
            return;
        if (packet.Paper)
        {
            Chart(from, packet.Cells);
            return;
        }
        int uncovered = 0;
        _applying = true;
        try
        {
            for (int i = 0; i + 1 < packet.Cells.Length; i += 2)
            {
                int x = packet.Cells[i], y = packet.Cells[i + 1];
                if (Fog(x, y) != 0)
                    continue;
                Game.CallScript("scr_globalmapFogUpdate", default, x, y);
                uncovered++;
            }
        }
        finally { _applying = false; }
        if (uncovered > 0)
            _context.Log($"{from.Name}'s world map: {uncovered} tile(s) uncovered here");
    }

    // Another's paper map put on ours: the tiles we haven't charted, by the game's own call, in one go (it redraws the
    // map once).
    private void Chart(RemotePlayer from, ushort[] cells)
    {
        var tiles = new List<GmValue>();
        for (int i = 0; i + 1 < cells.Length; i += 2)
            if (Paper(cells[i], cells[i + 1]) == 0)
            {
                tiles.Add(cells[i]);
                tiles.Add(cells[i + 1]);
            }
        if (tiles.Count == 0)
            return;
        // (Our paper map isn't made yet - just joined, the world map not drawn once: kept, and put on it once it is.)
        if (!PaperReady())
        {
            _pendingFrom = from;
            _pendingPaper.AddRange(cells);
            return;
        }
        _applying = true;
        try
        {
            using GmArray array = GmArray.From(tiles.ToArray());
            Game.CallScript("scr_globalmapPaperUpdateArray", default, array, true);
        }
        // (The game's script stopping on the tiles - an SEHException - mustn't drop the packet's handling with it.)
        catch (Exception e) when (e is System.Runtime.InteropServices.SEHException or InvalidOperationException)
        {
            _context.Log($"{from.Name}'s paper map: {tiles.Count / 2} tile(s) couldn't be charted here ({e.GetType().Name})");
            return;
        }
        finally { _applying = false; }
        _context.Log($"{from.Name}'s paper map: {tiles.Count / 2} tile(s) charted here");
    }

    private static bool PaperReady()
    {
        try
        {
            return Game.CallScript("scr_globalmapPaperExists", default).AsBool;
        }
        catch (System.Runtime.InteropServices.SEHException)
        {
            return false;
        }
    }

    // A tile's fog: 0 covered, 1 uncovered (-4: no world map).
    private static double Fog(int x, int y) => Game.CallScript("scr_globalmapFogGet", default, x, y).AsReal;

    // A tile on the paper map: 0 not charted, 1 charted.
    private static double Paper(int x, int y) => Game.CallScript("scr_globalmapPaperGet", default, x, y).AsReal;

    // Every tile we've uncovered (or charted): the game's own saved form of the grid (scr_globalmapFogSave,
    // scr_globalmapPaperSave: a digit a tile, column by column), read in one call.
    private static ushort[] All(string save, string saved)
    {
        int width = Game.Global["worldWidth"].AsInt, height = Game.Global["worldHeight"].AsInt;
        if (width <= 0 || height <= 0)
            return Array.Empty<ushort>();
        Game.CallScript(save, default);
        string fog = Game.Global[saved].AsString;
        var cells = new List<ushort>();
        for (int i = 0; i < fog.Length && i < width * height; i++)
            if (fog[i] == '1')
            {
                cells.Add((ushort)(i / height));
                cells.Add((ushort)(i % height));
            }
        return cells.ToArray();
    }
}
