using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Terraria;
using TShockAPI;
using TShockAPI.DB;

namespace SimpleRegions
{
    internal static class SimpleRegionsCommands
    {
        private static readonly List<Command> Registered = new List<Command>();

        private const string Pos1Key = "SimpleRegions.Pos1";
        private const string Pos2Key = "SimpleRegions.Pos2";

        private static readonly Regex NameRegex = new Regex(@"^[\w\-Ѐ-ӿ]+$", RegexOptions.Compiled);

        public static void Register()
        {
            Deregister();

            // Открыта без права: конкретные подкоманды проверяют свои права внутри,
            // чтобы игрок без simpleregions.claim всё же мог посмотреть /rg info.
            var cmd = new Command(CmdRegion, "rg", "regions")
            {
                HelpText = "/rg claim|pos1|pos2|list|info|add|remove|delete|show — управление приватами"
            };
            Commands.ChatCommands.Add(cmd);
            Registered.Add(cmd);
        }

        public static void Deregister()
        {
            foreach (var c in Registered)
                Commands.ChatCommands.Remove(c);
            Registered.Clear();
        }

        private static void CmdRegion(CommandArgs args)
        {
            var p = SimpleRegionsPlugin.Instance;
            if (p == null) return;

            if (!p.Config.Enabled)
            {
                args.Player.SendInfoMessage(p.Config.Messages.Disabled);
                return;
            }
            if (!p.Ready)
            {
                args.Player.SendErrorMessage("SimpleRegions ещё не инициализирован.");
                return;
            }
            if (!args.Player.RealPlayer)
            {
                args.Player.SendErrorMessage("Эта команда доступна только в игре.");
                return;
            }

            var sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "";
            switch (sub)
            {
                case "pos1": CmdPos(args, 1); return;
                case "pos2": CmdPos(args, 2); return;
                case "claim": CmdClaim(args); return;
                case "list": CmdList(args); return;
                case "info": CmdInfo(args); return;
                case "add": CmdAdd(args); return;
                case "remove": CmdRemove(args); return;
                case "delete": case "del": CmdDelete(args); return;
                case "show": CmdShow(args); return;
                default: SendUsage(args); return;
            }
        }

        private static void SendUsage(CommandArgs args)
        {
            args.Player.SendInfoMessage("Приваты (SimpleRegions):");
            args.Player.SendInfoMessage("  /rg pos1 и /rg pos2 — отметить углы участка");
            args.Player.SendInfoMessage("  /rg claim <имя> — заприватить отмеченный участок");
            args.Player.SendInfoMessage("  /rg claim <имя> <радиус> — квадрат вокруг себя");
            args.Player.SendInfoMessage("  /rg list — ваши приваты и остаток бюджета");
            args.Player.SendInfoMessage("  /rg info — что за приват здесь");
            args.Player.SendInfoMessage("  /rg add <ник> <имя> — пустить игрока строить");
            args.Player.SendInfoMessage("  /rg remove <ник> <имя> — убрать доступ");
            args.Player.SendInfoMessage("  /rg delete <имя> — удалить свой приват");
            args.Player.SendInfoMessage("  /rg show — подсветка границ");
        }

        // ------------------------------------------------------------------
        // Selection
        // ------------------------------------------------------------------

        private static void CmdPos(CommandArgs args, int which)
        {
            var p = SimpleRegionsPlugin.Instance;
            if (!RequirePermission(args, SimpleRegionsPlugin.PermClaim)) return;

            var x = args.Player.TileX;
            var y = args.Player.TileY;

            if (which == 1)
            {
                args.Player.SetData(Pos1Key, new Point(x, y));
                args.Player.SendSuccessMessage(SimpleRegionsPlugin.Format(p.Config.Messages.Pos1Set, x, y));

                // Marker only until the second corner is known.
                p.Viz.SetSelection(args.Player.Index, new Rect(x, y, x, y));
                return;
            }

            args.Player.SetData(Pos2Key, new Point(x, y));

            var pos1 = args.Player.GetData<Point>(Pos1Key);
            if (pos1.Equals(default(Point)))
            {
                args.Player.SendInfoMessage("Второй угол отмечен. Теперь отметьте первый: /rg pos1");
                return;
            }

            var rect = new Rect(pos1.X, pos1.Y, x, y);
            args.Player.SendSuccessMessage(SimpleRegionsPlugin.Format(
                p.Config.Messages.Pos2Set, x, y, rect.Width, rect.Height, rect.Area));

            // Preview the exact rectangle before it is committed, plus whether it is affordable.
            p.Viz.SetSelection(args.Player.Index, rect);
            args.Player.SendInfoMessage(p.Config.Messages.SelectionPreview);
            ReportAffordability(args, rect);
        }

        private static void ReportAffordability(CommandArgs args, Rect rect)
        {
            var p = SimpleRegionsPlugin.Instance;
            if (args.Player.HasPermission(SimpleRegionsPlugin.PermAdmin)) return;

            var existing = p.GetPlayerRegions(args.Player.Name).Select(r => r.Bounds).ToList();
            var used = AreaMath.UnionArea(existing);
            var cost = AreaMath.AdditionalCost(existing, rect);
            var free = p.Config.AreaBudgetPerPlayer - used;

            var msg = cost <= free
                ? SimpleRegionsPlugin.Format(p.Config.Messages.SelectionFits, cost, free, p.Config.AreaBudgetPerPlayer)
                : SimpleRegionsPlugin.Format(p.Config.Messages.SelectionDoesNotFit, cost, free, p.Config.AreaBudgetPerPlayer);
            args.Player.SendInfoMessage(msg);
        }

        // ------------------------------------------------------------------
        // Claim
        // ------------------------------------------------------------------

        private static void CmdClaim(CommandArgs args)
        {
            var p = SimpleRegionsPlugin.Instance;
            if (!RequirePermission(args, SimpleRegionsPlugin.PermClaim)) return;

            if (args.Parameters.Count < 2)
            {
                args.Player.SendErrorMessage("Использование: /rg claim <имя> [радиус]");
                return;
            }

            var name = args.Parameters[1];
            if (!IsValidName(name, p.Config.MaxRegionNameLength))
            {
                args.Player.SendErrorMessage(SimpleRegionsPlugin.Format(p.Config.Messages.BadName, p.Config.MaxRegionNameLength));
                return;
            }

            Rect rect;
            if (args.Parameters.Count >= 3)
            {
                if (!int.TryParse(args.Parameters[2], out var radius) || radius <= 0)
                {
                    args.Player.SendErrorMessage("Радиус должен быть положительным числом.");
                    return;
                }
                var cx = args.Player.TileX;
                var cy = args.Player.TileY;
                rect = new Rect(cx - radius, cy - radius, cx + radius, cy + radius);
            }
            else
            {
                var pos1 = args.Player.GetData<Point>(Pos1Key);
                var pos2 = args.Player.GetData<Point>(Pos2Key);
                if (pos1.Equals(default(Point)) || pos2.Equals(default(Point)))
                {
                    args.Player.SendErrorMessage(p.Config.Messages.NeedBothCorners);
                    return;
                }
                rect = new Rect(pos1.X, pos1.Y, pos2.X, pos2.Y);
            }

            if (!ValidateAndCreate(args, name, rect)) return;

            // Selection consumed — drop it and its preview.
            args.Player.SetData(Pos1Key, default(Point));
            args.Player.SetData(Pos2Key, default(Point));
            p.Viz.SetSelection(args.Player.Index, null);
        }

        private static bool ValidateAndCreate(CommandArgs args, string name, Rect rect)
        {
            var p = SimpleRegionsPlugin.Instance;
            var cfg = p.Config;
            var isAdmin = args.Player.HasPermission(SimpleRegionsPlugin.PermAdmin);

            // Clamp to the world so a claim can never run off the edge.
            rect = new Rect(
                Math.Max(1, rect.X1), Math.Max(1, rect.Y1),
                Math.Min(Main.maxTilesX - 2, rect.X2), Math.Min(Main.maxTilesY - 2, rect.Y2));

            if (!isAdmin)
            {
                if (rect.Width < cfg.MinRegionSize || rect.Height < cfg.MinRegionSize)
                {
                    args.Player.SendErrorMessage(SimpleRegionsPlugin.Format(
                        cfg.Messages.TooSmall, cfg.MinRegionSize, rect.Width, rect.Height));
                    return false;
                }
                if (rect.Width > cfg.MaxRegionSize || rect.Height > cfg.MaxRegionSize)
                {
                    args.Player.SendErrorMessage(SimpleRegionsPlugin.Format(
                        cfg.Messages.TooBig, cfg.MaxRegionSize, rect.Width, rect.Height));
                    return false;
                }

                if (cfg.SpawnProtectionRadius > 0)
                {
                    var dist = DistanceToSpawn(rect);
                    if (dist < cfg.SpawnProtectionRadius)
                    {
                        args.Player.SendErrorMessage(SimpleRegionsPlugin.Format(
                            cfg.Messages.TooCloseToSpawn, cfg.SpawnProtectionRadius, dist));
                        return false;
                    }
                }
            }

            if (TShock.Regions.GetRegionByName(name) != null)
            {
                args.Player.SendErrorMessage(SimpleRegionsPlugin.Format(cfg.Messages.NameTaken, name));
                return false;
            }

            // Overlap rules: own claims may overlap (that is how L-shapes are built),
            // anything else may not.
            var claimNames = p.GetClaimNames();
            foreach (var region in TShock.Regions.Regions.Where(r => r.WorldID == p.WorldId))
            {
                var bounds = AreaMath.FromTShockRect(region.Area.X, region.Area.Y, region.Area.Width, region.Area.Height);
                if (!bounds.Intersects(rect)) continue;

                var isPluginClaim = claimNames.Contains(region.Name);
                var isOwn = string.Equals(region.Owner, args.Player.Name, StringComparison.OrdinalIgnoreCase);

                if (isPluginClaim && isOwn) continue;

                if (isAdmin) continue;   // admins bypass the overlap restriction as well

                args.Player.SendErrorMessage(isPluginClaim
                    ? SimpleRegionsPlugin.Format(cfg.Messages.OverlapsForeign, region.Name,
                        string.IsNullOrEmpty(region.Owner) ? "неизвестен" : region.Owner)
                    : SimpleRegionsPlugin.Format(cfg.Messages.OverlapsAdmin, region.Name));
                return false;
            }

            // Budget: charge only land the player does not already own.
            var existing = p.GetPlayerRegions(args.Player.Name).Select(r => r.Bounds).ToList();
            var used = AreaMath.UnionArea(existing);
            var cost = AreaMath.AdditionalCost(existing, rect);
            var free = cfg.AreaBudgetPerPlayer - used;

            if (!isAdmin)
            {
                if (free <= 0)
                {
                    args.Player.SendErrorMessage(SimpleRegionsPlugin.Format(
                        cfg.Messages.BudgetExhausted, cfg.AreaBudgetPerPlayer));
                    return false;
                }
                if (cost > free)
                {
                    args.Player.SendErrorMessage(SimpleRegionsPlugin.Format(
                        cfg.Messages.NotEnoughBudget, cost, free, cfg.AreaBudgetPerPlayer));
                    return false;
                }
            }

            // TShock stores width/height one less than the protected tile count.
            AreaMath.ToTShockSize(rect, out var storedW, out var storedH);

            if (!TShock.Regions.AddRegion(rect.X1, rect.Y1, storedW, storedH, name, args.Player.Name, p.WorldId))
            {
                args.Player.SendErrorMessage(cfg.Messages.CreateFailed);
                TShock.Log.ConsoleError("[SimpleRegions] AddRegion вернул false для '" + name + "' (игрок " + args.Player.Name + ").");
                return false;
            }

            try
            {
                p.Db.AddClaim(name, p.WorldId, args.Player.Name);
            }
            catch (Exception ex)
            {
                // Roll the region back so we never leave a claim TShock knows about but the
                // plugin does not — that region would be invisible to /rg list and to budgeting.
                TShock.Regions.DeleteRegion(name);
                args.Player.SendErrorMessage(cfg.Messages.CreateFailed);
                TShock.Log.ConsoleError("[SimpleRegions] Не удалось записать приват в БД, регион откачен: " + ex.Message);
                return false;
            }

            var newUsed = used + cost;
            var remaining = Math.Max(0, cfg.AreaBudgetPerPlayer - newUsed);

            args.Player.SendSuccessMessage(cost == 0
                ? SimpleRegionsPlugin.Format(cfg.Messages.ClaimedFree, name, rect.Area, remaining, cfg.AreaBudgetPerPlayer)
                : SimpleRegionsPlugin.Format(cfg.Messages.Claimed, name, cost, remaining, cfg.AreaBudgetPerPlayer));

            TShock.Log.ConsoleInfo("[SimpleRegions] " + args.Player.Name + " создал приват '" + name + "' " +
                                   rect + " (" + rect.Area + " блоков, списано " + cost + ").");
            return true;
        }

        private static int DistanceToSpawn(Rect rect)
        {
            var sx = Main.spawnTileX;
            var sy = Main.spawnTileY;

            // Distance from spawn to the nearest edge of the rect (0 if spawn is inside).
            var dx = sx < rect.X1 ? rect.X1 - sx : (sx > rect.X2 ? sx - rect.X2 : 0);
            var dy = sy < rect.Y1 ? rect.Y1 - sy : (sy > rect.Y2 ? sy - rect.Y2 : 0);
            return (int)Math.Sqrt((double)dx * dx + (double)dy * dy);
        }

        private static bool IsValidName(string name, int maxLength)
        {
            return !string.IsNullOrWhiteSpace(name) && name.Length <= maxLength && NameRegex.IsMatch(name);
        }

        // ------------------------------------------------------------------
        // List / info
        // ------------------------------------------------------------------

        private static void CmdList(CommandArgs args)
        {
            var p = SimpleRegionsPlugin.Instance;
            var regions = p.GetPlayerRegions(args.Player.Name);

            if (regions.Count == 0)
            {
                args.Player.SendInfoMessage(p.Config.Messages.ListEmpty);
                return;
            }

            args.Player.SendInfoMessage(SimpleRegionsPlugin.Format(p.Config.Messages.ListHeader, regions.Count));

            var all = regions.Select(r => r.Bounds).ToList();
            var totalUsed = AreaMath.UnionArea(all);

            foreach (var region in regions)
            {
                // A region's budget contribution is what would be freed by deleting it —
                // land also covered by another of the player's claims stays paid for, so
                // for overlapping claims this is smaller than the raw area.
                var others = regions.Where(r => r != region).Select(r => r.Bounds).ToList();
                var contribution = totalUsed - AreaMath.UnionArea(others);

                args.Player.SendMessage(SimpleRegionsPlugin.Format(p.Config.Messages.ListLine,
                    region.Name, region.Bounds.Width, region.Bounds.Height, region.Bounds.Area, contribution), 255, 255, 255);

                var members = GetMemberNames(region.TShockRegion);
                args.Player.SendMessage(members.Count > 0
                    ? SimpleRegionsPlugin.Format(p.Config.Messages.ListLineMembers, string.Join(", ", members))
                    : p.Config.Messages.ListLineNoMembers, 200, 200, 200);
            }

            var free = Math.Max(0, p.Config.AreaBudgetPerPlayer - totalUsed);
            args.Player.SendInfoMessage(SimpleRegionsPlugin.Format(
                p.Config.Messages.ListFooter, totalUsed, p.Config.AreaBudgetPerPlayer, free));
        }

        private static List<string> GetMemberNames(Region region)
        {
            var names = new List<string>();
            if (region?.AllowedIDs == null) return names;

            foreach (var id in region.AllowedIDs)
            {
                try
                {
                    var account = TShock.UserAccounts.GetUserAccountByID(id);
                    names.Add(account != null ? account.Name : "id:" + id);
                }
                catch
                {
                    names.Add("id:" + id);
                }
            }
            return names;
        }

        private static void CmdInfo(CommandArgs args)
        {
            var p = SimpleRegionsPlugin.Instance;
            var x = args.Player.TileX;
            var y = args.Player.TileY;

            var here = TShock.Regions.Regions
                .Where(r => r.WorldID == p.WorldId && r.InArea(x, y))
                .ToList();

            if (here.Count == 0)
            {
                args.Player.SendInfoMessage(p.Config.Messages.InfoNone);
                return;
            }

            if (here.Count == 1)
            {
                var only = here[0];
                args.Player.SendInfoMessage(SimpleRegionsPlugin.Format(p.Config.Messages.InfoSingle,
                    only.Name, string.IsNullOrEmpty(only.Owner) ? "сервер" : only.Owner));
                return;
            }

            args.Player.SendInfoMessage(SimpleRegionsPlugin.Format(p.Config.Messages.InfoOverlapHeader, here.Count));
            foreach (var region in here)
                args.Player.SendMessage(SimpleRegionsPlugin.Format(p.Config.Messages.InfoOverlapLine,
                    region.Name, string.IsNullOrEmpty(region.Owner) ? "сервер" : region.Owner), 255, 255, 255);
            args.Player.SendInfoMessage(p.Config.Messages.InfoOverlapNote);
        }

        // ------------------------------------------------------------------
        // Members
        // ------------------------------------------------------------------

        private static void CmdAdd(CommandArgs args) => ChangeMember(args, true);
        private static void CmdRemove(CommandArgs args) => ChangeMember(args, false);

        private static void ChangeMember(CommandArgs args, bool add)
        {
            var p = SimpleRegionsPlugin.Instance;
            if (!RequirePermission(args, SimpleRegionsPlugin.PermClaim)) return;

            if (args.Parameters.Count < 3)
            {
                args.Player.SendErrorMessage(add
                    ? "Использование: /rg add <ник> <имя привата>"
                    : "Использование: /rg remove <ник> <имя привата>");
                return;
            }

            var targetName = args.Parameters[1];
            var regionName = string.Join(" ", args.Parameters.Skip(2));

            var region = ResolveOwnRegion(args, regionName);
            if (region == null) return;

            var account = TShock.UserAccounts.GetUserAccountByName(targetName);
            if (account == null)
            {
                args.Player.SendErrorMessage(SimpleRegionsPlugin.Format(p.Config.Messages.MemberNotFound, targetName));
                return;
            }

            var already = region.TShockRegion.AllowedIDs != null && region.TShockRegion.AllowedIDs.Contains(account.ID);

            if (add)
            {
                if (already)
                {
                    args.Player.SendInfoMessage(SimpleRegionsPlugin.Format(
                        p.Config.Messages.MemberAlreadyAdded, account.Name, region.Name));
                    return;
                }
                TShock.Regions.AddNewUser(region.Name, account.Name);
                args.Player.SendSuccessMessage(SimpleRegionsPlugin.Format(
                    p.Config.Messages.MemberAdded, account.Name, region.Name));
            }
            else
            {
                if (!already)
                {
                    args.Player.SendInfoMessage(SimpleRegionsPlugin.Format(
                        p.Config.Messages.MemberNotInRegion, account.Name, region.Name));
                    return;
                }
                TShock.Regions.RemoveUser(region.Name, account.Name);
                args.Player.SendSuccessMessage(SimpleRegionsPlugin.Format(
                    p.Config.Messages.MemberRemoved, account.Name, region.Name));
            }
        }

        // ------------------------------------------------------------------
        // Delete
        // ------------------------------------------------------------------

        private static void CmdDelete(CommandArgs args)
        {
            var p = SimpleRegionsPlugin.Instance;
            if (!RequirePermission(args, SimpleRegionsPlugin.PermClaim)) return;

            if (args.Parameters.Count < 2)
            {
                args.Player.SendErrorMessage("Использование: /rg delete <имя привата>");
                return;
            }

            var regionName = string.Join(" ", args.Parameters.Skip(1));
            var region = ResolveOwnRegion(args, regionName);
            if (region == null) return;

            var owner = region.Owner;
            var freedFrom = p.GetPlayerRegions(owner).Select(r => r.Bounds).ToList();
            var usedBefore = AreaMath.UnionArea(freedFrom);

            if (!TShock.Regions.DeleteRegion(region.Name))
            {
                args.Player.SendErrorMessage("Не удалось удалить регион — подробности в консоли сервера.");
                TShock.Log.ConsoleError("[SimpleRegions] DeleteRegion вернул false для '" + region.Name + "'.");
                return;
            }

            p.Db.RemoveClaim(region.Name, p.WorldId);

            var usedAfter = AreaMath.UnionArea(p.GetPlayerRegions(owner).Select(r => r.Bounds).ToList());
            var freed = usedBefore - usedAfter;

            args.Player.SendSuccessMessage(SimpleRegionsPlugin.Format(p.Config.Messages.Deleted,
                region.Name, freed, usedAfter, p.Config.AreaBudgetPerPlayer));

            TShock.Log.ConsoleInfo("[SimpleRegions] " + args.Player.Name + " удалил приват '" + region.Name + "'.");
        }

        /// <summary>
        /// Finds one of the caller's own claims by name. Admins may target anyone's claim;
        /// everyone else is restricted to their own, so players can never touch foreign land.
        /// </summary>
        private static PluginRegion ResolveOwnRegion(CommandArgs args, string regionName)
        {
            var p = SimpleRegionsPlugin.Instance;
            var isAdmin = args.Player.HasPermission(SimpleRegionsPlugin.PermAdmin);

            var candidates = isAdmin ? p.GetAllPluginRegions() : p.GetPlayerRegions(args.Player.Name);
            var region = candidates.FirstOrDefault(r => string.Equals(r.Name, regionName, StringComparison.OrdinalIgnoreCase));

            if (region != null) return region;

            // Distinguish "not yours" from "does not exist" so the message is actionable.
            var anyRegion = p.GetAllPluginRegions()
                .FirstOrDefault(r => string.Equals(r.Name, regionName, StringComparison.OrdinalIgnoreCase));

            args.Player.SendErrorMessage(anyRegion != null
                ? SimpleRegionsPlugin.Format(p.Config.Messages.NotYourRegion, regionName)
                : SimpleRegionsPlugin.Format(p.Config.Messages.RegionNotFound, regionName));
            return null;
        }

        // ------------------------------------------------------------------
        // Show
        // ------------------------------------------------------------------

        private static void CmdShow(CommandArgs args)
        {
            var p = SimpleRegionsPlugin.Instance;
            if (!RequirePermission(args, SimpleRegionsPlugin.PermShow)) return;

            var on = p.Viz.ToggleShow(args.Player.Index);
            args.Player.SendSuccessMessage(on
                ? SimpleRegionsPlugin.Format(p.Config.Messages.ShowOn, p.Config.HighlightRadius)
                : p.Config.Messages.ShowOff);
        }

        private static bool RequirePermission(CommandArgs args, string permission)
        {
            if (args.Player.HasPermission(permission) || args.Player.HasPermission(SimpleRegionsPlugin.PermAdmin))
                return true;
            args.Player.SendErrorMessage("Нужно право " + permission + ".");
            return false;
        }
    }
}
