using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Terraria;
using TShockAPI;

namespace SimpleRegions
{
    /// <summary>
    /// Draws claim borders for a single player by sending them tile data that differs from
    /// the real world: the tile's paint colour is flipped, the packet is built, and the real
    /// paint is restored immediately — all within one synchronous block on the main thread.
    /// The world is never actually modified and nobody else sees anything.
    ///
    /// Threading: every tile touch happens on the main thread. Commands run on network I/O
    /// threads, so they only enqueue work here and <see cref="Pump"/> (called from
    /// GameUpdate) performs it.
    /// </summary>
    internal class Visualizer
    {
        private class PlayerViz
        {
            public bool ShowMode;
            public Rect? Selection;
            public DateTime NextRefresh;
            /// <summary>Rects we painted last time, so we can repaint them with real data to clear.</summary>
            public List<(int X, int Y, int W, int H)> Sent = new List<(int, int, int, int)>();
            /// <summary>Packed tile coords currently shown as painted — used to detect interaction.</summary>
            public HashSet<long> Tiles = new HashSet<long>();
        }

        private readonly Dictionary<int, PlayerViz> _state = new Dictionary<int, PlayerViz>();
        private readonly object _stateLock = new object();
        private readonly ConcurrentQueue<Action> _pending = new ConcurrentQueue<Action>();

        private SimpleRegionsConfig Config => SimpleRegionsPlugin.Instance.Config;

        private static long Pack(int x, int y) => ((long)x << 32) | (uint)y;

        private PlayerViz GetOrCreate(int index)
        {
            lock (_stateLock)
            {
                if (!_state.TryGetValue(index, out var viz))
                {
                    viz = new PlayerViz();
                    _state[index] = viz;
                }
                return viz;
            }
        }

        private PlayerViz GetOrNull(int index)
        {
            lock (_stateLock)
            {
                _state.TryGetValue(index, out var viz);
                return viz;
            }
        }

        // ------------------------------------------------------------------
        // Public API (safe to call from any thread)
        // ------------------------------------------------------------------

        public bool IsShowing(int index)
        {
            var viz = GetOrNull(index);
            return viz != null && viz.ShowMode;
        }

        public bool ToggleShow(int index)
        {
            var viz = GetOrCreate(index);
            viz.ShowMode = !viz.ShowMode;
            var on = viz.ShowMode;

            _pending.Enqueue(() =>
            {
                var player = PlayerByIndex(index);
                if (player == null) return;
                Clear(player, viz);
                if (on) Redraw(player, viz);
            });

            return on;
        }

        public void SetSelection(int index, Rect? selection)
        {
            var viz = GetOrCreate(index);
            viz.Selection = selection;

            _pending.Enqueue(() =>
            {
                var player = PlayerByIndex(index);
                if (player == null) return;
                Clear(player, viz);
                Redraw(player, viz);
            });
        }

        /// <summary>True if this tile is currently faked for that player (so we should resync).</summary>
        public bool IsHighlighted(int index, int x, int y)
        {
            var viz = GetOrNull(index);
            if (viz == null) return false;
            lock (viz.Tiles)
                return viz.Tiles.Contains(Pack(x, y));
        }

        /// <summary>Drops all fake tiles for a player and turns the modes off.</summary>
        public void ClearFor(int index, bool alsoDisableModes)
        {
            var viz = GetOrNull(index);
            if (viz == null) return;

            if (alsoDisableModes)
            {
                viz.ShowMode = false;
                viz.Selection = null;
            }

            _pending.Enqueue(() =>
            {
                var player = PlayerByIndex(index);
                if (player == null) return;
                Clear(player, viz);
            });
        }

        public void Forget(int index)
        {
            lock (_stateLock)
                _state.Remove(index);
        }

        /// <summary>Restores real tiles for everyone — used on plugin unload.</summary>
        public void ClearAll()
        {
            List<KeyValuePair<int, PlayerViz>> all;
            lock (_stateLock)
                all = new List<KeyValuePair<int, PlayerViz>>(_state);

            foreach (var kv in all)
            {
                var player = PlayerByIndex(kv.Key);
                if (player != null) Clear(player, kv.Value);
            }

            lock (_stateLock)
                _state.Clear();
        }

        // ------------------------------------------------------------------
        // Main thread
        // ------------------------------------------------------------------

        /// <summary>Called every tick from GameUpdate: runs queued work and refreshes highlights.</summary>
        public void Pump()
        {
            while (_pending.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { TShock.Log.ConsoleError("[SimpleRegions] Ошибка отрисовки: " + ex.Message); }
            }

            var now = DateTime.UtcNow;
            List<KeyValuePair<int, PlayerViz>> all;
            lock (_stateLock)
                all = new List<KeyValuePair<int, PlayerViz>>(_state);

            foreach (var kv in all)
            {
                var viz = kv.Value;
                if (!viz.ShowMode && !viz.Selection.HasValue) continue;
                if (now < viz.NextRefresh) continue;

                viz.NextRefresh = now.AddSeconds(Config.HighlightRefreshSeconds);

                var player = PlayerByIndex(kv.Key);
                if (player == null || !player.Active) continue;

                try
                {
                    // Clients silently drop our fake tiles whenever they re-request a chunk
                    // (walking away and back, section reload), so the highlight has to be
                    // re-sent periodically or it just quietly disappears.
                    Clear(player, viz);
                    Redraw(player, viz);
                }
                catch (Exception ex)
                {
                    TShock.Log.ConsoleError("[SimpleRegions] Ошибка обновления подсветки: " + ex.Message);
                }
            }
        }

        private static TSPlayer PlayerByIndex(int index)
        {
            if (index < 0 || index >= TShock.Players.Length) return null;
            var p = TShock.Players[index];
            return p != null && p.Active ? p : null;
        }

        /// <summary>Re-sends the previously painted areas with their real data.</summary>
        private void Clear(TSPlayer player, PlayerViz viz)
        {
            List<(int X, int Y, int W, int H)> sent;
            lock (viz.Sent)
            {
                sent = new List<(int, int, int, int)>(viz.Sent);
                viz.Sent.Clear();
            }
            lock (viz.Tiles)
                viz.Tiles.Clear();

            foreach (var (x, y, w, h) in sent)
                player.SendTileRect((short)x, (short)y, (byte)w, (byte)h);
        }

        private void Redraw(TSPlayer player, PlayerViz viz)
        {
            var cfg = Config;
            var radius = cfg.HighlightRadius;
            var view = new Rect(
                player.TileX - radius, player.TileY - radius,
                player.TileX + radius, player.TileY + radius);

            if (viz.Selection.HasValue)
                DrawBorder(player, viz, viz.Selection.Value, view, cfg.PaintSelection);

            if (viz.ShowMode)
            {
                var plugin = SimpleRegionsPlugin.Instance;
                foreach (var region in plugin.GetPluginRegionsInView(view))
                {
                    var isOwn = string.Equals(region.Owner, player.Name, StringComparison.OrdinalIgnoreCase);
                    DrawBorder(player, viz, region.Bounds, view,
                        isOwn ? cfg.PaintOwnRegion : cfg.PaintForeignRegion);
                }
            }
        }

        /// <summary>
        /// Paints just the four edge strips of a rect — not the whole box — so a 100x100
        /// claim costs a handful of tiny packets instead of one 10,000-tile blob.
        /// </summary>
        private void DrawBorder(TSPlayer player, PlayerViz viz, Rect rect, Rect view, byte paint)
        {
            var strips = new List<(int X, int Y, int W, int H)>
            {
                (rect.X1, rect.Y1, rect.Width, 1),                              // top
                (rect.X1, rect.Y2, rect.Width, 1),                              // bottom
                (rect.X1, rect.Y1 + 1, 1, Math.Max(0, rect.Height - 2)),        // left
                (rect.X2, rect.Y1 + 1, 1, Math.Max(0, rect.Height - 2))         // right
            };

            foreach (var strip in strips)
            {
                if (strip.W <= 0 || strip.H <= 0) continue;

                // Clip to the highlight radius and to the world.
                var x1 = Math.Max(Math.Max(strip.X, view.X1), 1);
                var y1 = Math.Max(Math.Max(strip.Y, view.Y1), 1);
                var x2 = Math.Min(Math.Min(strip.X + strip.W - 1, view.X2), Main.maxTilesX - 2);
                var y2 = Math.Min(Math.Min(strip.Y + strip.H - 1, view.Y2), Main.maxTilesY - 2);
                if (x1 > x2 || y1 > y2) continue;

                var chunk = Config.HighlightChunkSize;
                for (var cy = y1; cy <= y2; cy += chunk)
                {
                    for (var cx = x1; cx <= x2; cx += chunk)
                    {
                        var w = Math.Min(chunk, x2 - cx + 1);
                        var h = Math.Min(chunk, y2 - cy + 1);
                        PaintAndSend(player, viz, cx, cy, w, h, paint);
                    }
                }
            }
        }

        /// <summary>
        /// The core trick: flip paint on the real tiles, build+send the packet for this one
        /// player, then put the real paint back. SendTileRect serialises the tile data
        /// synchronously, so by the time it returns the world is already untouched again.
        /// </summary>
        private void PaintAndSend(TSPlayer player, PlayerViz viz, int x, int y, int w, int h, byte paint)
        {
            var originals = new byte[w, h];
            var painted = new bool[w, h];

            for (var i = 0; i < w; i++)
            {
                for (var j = 0; j < h; j++)
                {
                    var tile = Main.tile[x + i, y + j];
                    if (tile == null || !tile.active()) continue;   // paint is invisible on air

                    originals[i, j] = tile.color();
                    tile.color(paint);
                    painted[i, j] = true;
                }
            }

            player.SendTileRect((short)x, (short)y, (byte)w, (byte)h);

            for (var i = 0; i < w; i++)
                for (var j = 0; j < h; j++)
                    if (painted[i, j])
                        Main.tile[x + i, y + j].color(originals[i, j]);

            lock (viz.Sent)
                viz.Sent.Add((x, y, w, h));

            lock (viz.Tiles)
                for (var i = 0; i < w; i++)
                    for (var j = 0; j < h; j++)
                        if (painted[i, j])
                            viz.Tiles.Add(Pack(x + i, y + j));
        }
    }
}
