using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.Hooks;

namespace SimpleRegions
{
    /// <summary>A claim: the TShock region plus the plugin's own view of it.</summary>
    public class PluginRegion
    {
        public string Name;
        public string Owner;
        public Rect Bounds;
        public Region TShockRegion;
    }

    /// <summary>
    /// SimpleRegions — players claim their own land in one or two commands, see the borders,
    /// and are limited by a land budget rather than a claim count.
    ///
    /// Claims are stored as ordinary TShock regions so they stay visible to /region list and
    /// fully manageable with the built-in admin tooling; this plugin only adds its own
    /// metadata table on the side.
    /// </summary>
    [ApiVersion(2, 1)]
    public class SimpleRegionsPlugin : TerrariaPlugin
    {
        public override string Name => "SimpleRegions";
        public override string Author => "Solevara";
        public override string Description => "Самообслуживание игроков по приватам с бюджетом площади и подсветкой границ";
        public override Version Version => new Version(1, 0, 0);

        public const string PermClaim = "simpleregions.claim";
        public const string PermShow = "simpleregions.show";
        public const string PermAdmin = "simpleregions.admin";

        internal static SimpleRegionsPlugin Instance;

        internal SimpleRegionsConfig Config = SimpleRegionsConfig.CreateDefault();
        internal SimpleRegionsDb Db;
        internal readonly Visualizer Viz = new Visualizer();

        private bool _ready;

        internal string WorldId => Main.worldID.ToString();
        internal bool Ready => _ready;

        private string ConfigPath => Path.Combine(TShock.SavePath, "SimpleRegions.json");

        public SimpleRegionsPlugin(Main game) : base(game)
        {
        }

        public override void Initialize()
        {
            Instance = this;

            LoadConfig(out _);

            ServerApi.Hooks.GamePostInitialize.Register(this, OnPostInitialize);
            ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
            ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);

            GetDataHandlers.TileEdit.Register(OnTileEdit);
            PlayerHooks.PlayerHasBuildPermission += OnPlayerHasBuildPermission;

            SimpleRegionsCommands.Register();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GamePostInitialize.Deregister(this, OnPostInitialize);
                ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnServerLeave);

                GetDataHandlers.TileEdit.UnRegister(OnTileEdit);
                PlayerHooks.PlayerHasBuildPermission -= OnPlayerHasBuildPermission;

                // Players must not be left staring at fake paint after an unload.
                try { Viz.ClearAll(); }
                catch (Exception ex) { TShock.Log.ConsoleError("[SimpleRegions] Ошибка снятия подсветки при выгрузке: " + ex.Message); }

                SimpleRegionsCommands.Deregister();
                Instance = null;
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        internal bool LoadConfig(out string error)
        {
            error = null;
            try
            {
                Config = SimpleRegionsConfig.Read(ConfigPath, out error);
            }
            catch (Exception ex)
            {
                Config = SimpleRegionsConfig.CreateDefault();
                error = ex.Message;
            }

            if (!string.IsNullOrEmpty(error))
            {
                TShock.Log.ConsoleError("[SimpleRegions] Проблемы с конфигом: " + error +
                                        ". Используются значения по умолчанию для проблемных полей.");
                return false;
            }
            return true;
        }

        private void OnPostInitialize(EventArgs args)
        {
            try
            {
                Db = new SimpleRegionsDb(TShock.DB);

                var names = new HashSet<string>(
                    TShock.Regions.Regions
                        .Where(r => r.WorldID == WorldId)
                        .Select(r => r.Name),
                    StringComparer.OrdinalIgnoreCase);

                Db.ReportIntegrity(WorldId, names);

                _ready = true;
                TShock.Log.ConsoleInfo("[SimpleRegions] Готов, мир " + WorldId + ".");
            }
            catch (Exception ex)
            {
                _ready = false;
                TShock.Log.ConsoleError("[SimpleRegions] Не удалось инициализировать БД: " + ex.Message +
                                        ". Приваты отключены, сервер продолжает работу.");
            }
        }

        private void OnUpdate(EventArgs args)
        {
            if (!_ready) return;
            try { Viz.Pump(); }
            catch (Exception ex) { TShock.Log.ConsoleError("[SimpleRegions] Ошибка в цикле обновления: " + ex.Message); }
        }

        private void OnServerLeave(LeaveEventArgs args)
        {
            // No need to un-paint a client that is gone, but the state must not leak.
            Viz.Forget(args.Who);
        }

        // ------------------------------------------------------------------
        // Overlap build permission
        // ------------------------------------------------------------------

        /// <summary>
        /// TShock's own CanBuild only consults the TOP region by Z, so where a player layers
        /// two of their own claims the higher one alone decides — which would silently revoke
        /// access granted in the other. Claims are meant to be layered to build L-shapes, so
        /// here the rule is: allowed in ANY overlapping claim means allowed.
        ///
        /// Deliberately narrow: if a non-plugin (admin) region covers the tile, this hook
        /// stays out of it and lets TShock decide, so plugin claims can never be used to
        /// punch a hole in an admin protection.
        /// </summary>
        private void OnPlayerHasBuildPermission(PlayerHasBuildPermissionEventArgs args)
        {
            if (!_ready || !Config.Enabled) return;
            if (args.Result != PermissionHookResult.Unhandled) return;
            if (args.Player == null) return;

            try
            {
                var regions = TShock.Regions.Regions
                    .Where(r => r.WorldID == WorldId && r.InArea(args.X, args.Y))
                    .ToList();

                if (regions.Count < 2) return;   // no layering here, nothing to correct

                var claims = GetClaimNames();
                if (regions.Any(r => !claims.Contains(r.Name)))
                    return;   // an admin region is involved — defer to TShock entirely

                if (regions.Any(r => r.HasPermissionToBuildInRegion(args.Player)))
                    args.Result = PermissionHookResult.Granted;
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("[SimpleRegions] Ошибка проверки прав на постройку: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Anti-desync for highlighted tiles
        // ------------------------------------------------------------------

        private void OnTileEdit(object sender, GetDataHandlers.TileEditEventArgs e)
        {
            if (!_ready || e.Handled || e.Player == null) return;

            try
            {
                if (!Viz.IsHighlighted(e.Player.Index, e.X, e.Y)) return;

                // The real tile was never modified, so the edit itself is safe to let through —
                // but the client still believes the tile is painted, so drop the highlight and
                // resend real data to keep both sides in step.
                Viz.ClearFor(e.Player.Index, true);
                e.Player.SendInfoMessage(Config.Messages.HighlightCleared);
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("[SimpleRegions] Ошибка в TileEdit: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Claim queries
        // ------------------------------------------------------------------

        internal HashSet<string> GetClaimNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Db.GetClaims(WorldId))
                set.Add(c.RegionName);
            return set;
        }

        /// <summary>All plugin-created claims that still exist as TShock regions in this world.</summary>
        internal List<PluginRegion> GetAllPluginRegions()
        {
            var result = new List<PluginRegion>();
            var claims = Db.GetClaims(WorldId);

            foreach (var claim in claims)
            {
                var region = TShock.Regions.GetRegionByName(claim.RegionName);
                if (region == null || region.WorldID != WorldId) continue;   // drift is only warned about at startup

                result.Add(new PluginRegion
                {
                    Name = region.Name,
                    // The region itself is the source of truth for ownership, so an admin
                    // /region owner change is picked up without touching our table.
                    Owner = string.IsNullOrEmpty(region.Owner) ? claim.Owner : region.Owner,
                    Bounds = AreaMath.FromTShockRect(region.Area.X, region.Area.Y, region.Area.Width, region.Area.Height),
                    TShockRegion = region
                });
            }

            return result;
        }

        internal List<PluginRegion> GetPlayerRegions(string playerName)
        {
            return GetAllPluginRegions()
                .Where(r => string.Equals(r.Owner, playerName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        internal List<PluginRegion> GetPluginRegionsInView(Rect view)
        {
            return GetAllPluginRegions().Where(r => r.Bounds.Intersects(view)).ToList();
        }

        internal long GetUsedBudget(string playerName)
        {
            var rects = GetPlayerRegions(playerName).Select(r => r.Bounds).ToList();
            return AreaMath.UnionArea(rects);
        }

        internal static string Format(string template, params object[] args)
        {
            if (string.IsNullOrEmpty(template)) return "";
            try { return string.Format(template, args); }
            catch (FormatException) { return template; }
        }
    }
}
