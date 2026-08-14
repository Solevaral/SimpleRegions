using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Terraria.ID;

namespace SimpleRegions
{
    /// <summary>All player-facing text. Russian by default, matching the target server.</summary>
    public class SimpleRegionsMessages
    {
        public string Disabled = "Приваты сейчас отключены.";

        // Selection
        public string Pos1Set = "Первый угол отмечен: ({0}, {1}). Теперь отметьте второй: /rg pos2";
        public string Pos2Set = "Второй угол отмечен: ({0}, {1}). Участок {2}x{3} = {4} блоков.";
        public string SelectionPreview = "Показано превью. Создать: /rg claim <имя>";
        public string SelectionFits = "Бюджета хватает: нужно {0}, свободно {1} из {2}.";
        public string SelectionDoesNotFit = "[c/ff5555:Бюджета не хватит:] нужно {0}, свободно {1} из {2}.";
        public string NeedBothCorners = "Сначала отметьте оба угла: /rg pos1 и /rg pos2 (или укажите радиус: /rg claim <имя> <радиус>).";

        // Claim results
        public string Claimed = "Приват '{0}' создан: {1} блоков. Осталось {2} из {3}.";
        public string ClaimedFree = "Приват '{0}' создан: {1} блоков (внутри уже занятой вами земли, бюджет не потрачен). Осталось {2} из {3}.";
        public string TooSmall = "Слишком маленький участок: минимум {0}x{0}, у вас {1}x{2}.";
        public string TooBig = "Слишком большой участок: максимум {0}x{0}, у вас {1}x{2}.";
        public string NotEnoughBudget = "Не хватает места: нужно {0} блоков, осталось {1} из {2}. Удалите ненужные приваты через /rg delete.";
        public string BudgetExhausted = "Бюджет исчерпан полностью ({0} из {0} занято) — приватить больше нельзя. Освободите место через /rg delete.";
        public string OverlapsForeign = "Участок пересекается с чужим приватом '{0}' (владелец: {1}). Выберите другое место.";
        public string OverlapsAdmin = "Участок пересекается со служебным регионом '{0}'. Выберите другое место.";
        public string NameTaken = "Регион с именем '{0}' уже существует. Выберите другое имя.";
        public string BadName = "Недопустимое имя: используйте буквы, цифры, дефис и подчёркивание (до {0} символов).";
        public string TooCloseToSpawn = "Слишком близко к спавну: нужно отойти минимум на {0} блоков (сейчас {1}).";
        public string CreateFailed = "Не удалось создать регион — подробности в консоли сервера.";

        // List / info
        public string ListHeader = "Ваши приваты ({0}):";
        public string ListLine = "  '{0}' — {1}x{2} = {3} блоков, вклад в бюджет: {4}";
        public string ListLineMembers = "     допущены: {0}";
        public string ListLineNoMembers = "     допущенных нет";
        public string ListFooter = "Занято {0} из {1} блоков (свободно {2}).";
        public string ListEmpty = "У вас пока нет приватов. Создайте первый: /rg pos1, /rg pos2, /rg claim <имя>";
        public string InfoNone = "Здесь нет приватов — земля свободна.";
        public string InfoSingle = "Здесь приват '{0}' (владелец: {1}).";
        public string InfoOverlapHeader = "Здесь {0} наслаивающихся привата(ов):";
        public string InfoOverlapLine = "  '{0}' (владелец: {1})";
        public string InfoOverlapNote = "Регионы наслаиваются друг на друга. Строить может тот, кто допущен хотя бы в один из них.";

        // Members
        public string MemberAdded = "Игрок {0} допущен в приват '{1}'.";
        public string MemberRemoved = "Доступ игрока {0} к привату '{1}' убран.";
        public string MemberNotFound = "Игрок с именем '{0}' не найден (нужен зарегистрированный аккаунт).";
        public string MemberAlreadyAdded = "Игрок {0} уже допущен в приват '{1}'.";
        public string MemberNotInRegion = "Игрок {0} и так не имеет доступа к привату '{1}'.";

        // Delete
        public string Deleted = "Приват '{0}' удалён. Освободилось {1} блоков, теперь занято {2} из {3}.";

        // Ownership / lookup
        public string NotYourRegion = "Приват '{0}' вам не принадлежит.";
        public string RegionNotFound = "У вас нет привата с именем '{0}'.";

        // Highlight
        public string ShowOn = "Подсветка границ включена (радиус {0} блоков). Свои приваты — одним цветом, чужие — другим.";
        public string ShowOnCount = "Рядом подсвечено приватов: {0}.";
        public string ShowOnNothing = "Рядом (в радиусе {0} блоков) приватов нет — подсвечивать нечего. Подойдите к привату или создайте новый.";
        public string ShowOff = "Подсветка границ выключена.";
        public string HighlightCleared = "Подсветка снята (вы взаимодействовали с подсвеченным блоком).";

        public string ConfigReloaded = "Конфиг SimpleRegions перезагружен.";
    }

    public class SimpleRegionsConfig
    {
        public bool Enabled = true;

        /// <summary>Total land (in tiles) one player may claim, counted as a UNION of their claims.</summary>
        public int AreaBudgetPerPlayer = 40000;

        /// <summary>Minimum side length of a single claim.</summary>
        public int MinRegionSize = 10;

        /// <summary>Maximum side length of a single claim.</summary>
        public int MaxRegionSize = 100;

        /// <summary>Claims closer than this to the world spawn are rejected. 0 disables the check.</summary>
        public int SpawnProtectionRadius = 0;

        /// <summary>How far from the player borders are highlighted. Keeps packet volume sane.</summary>
        public int HighlightRadius = 100;

        /// <summary>
        /// How often an active highlight is re-sent. Clients re-request real tiles when
        /// chunks reload, which silently wipes the highlight — this refresh restores it.
        /// </summary>
        public int HighlightRefreshSeconds = 4;

        /// <summary>Longest side of a single tile-rect packet used for highlighting.</summary>
        public int HighlightChunkSize = 50;

        public int MaxRegionNameLength = 32;

        /// <summary>
        /// Marker WALL drawn (for that one client only) where a border crosses empty space.
        ///
        /// It must be a wall, not a block: walls have no collision. Faking a solid block in
        /// mid-air makes the client collide with a phantom the server does not have — players
        /// stand on air, get stuck, and desync.
        /// </summary>
        public int HighlightAirWall = WallID.Glass;

        /// <summary>
        /// Render highlighted tiles fullbright, so a border stays readable in unlit caves and
        /// at night instead of being just another shade of dark.
        /// </summary>
        public bool HighlightGlow = true;

        // Paint colours used for the fake-tile highlight. Combined with HighlightGlow these
        // read as a shimmering border on your own land and a lava-coloured one on someone
        // else's — the look of those liquids, with none of their behaviour. Actual liquids
        // cannot be used: a client that sees lava applies lava damage locally and reports it
        // to the server, so a purely visual "lava" would really burn the player.
        public byte PaintOwnRegion = PaintID.CyanPaint;
        public byte PaintForeignRegion = PaintID.DeepOrangePaint;
        public byte PaintSelection = PaintID.YellowPaint;
        public byte PaintCorner = PaintID.CyanPaint;

        public SimpleRegionsMessages Messages = new SimpleRegionsMessages();

        public static SimpleRegionsConfig CreateDefault() => new SimpleRegionsConfig();

        public static SimpleRegionsConfig Read(string path, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path))
                {
                    var def = CreateDefault();
                    def.Write(path);
                    return def;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                var cfg = JsonConvert.DeserializeObject<SimpleRegionsConfig>(json);
                if (cfg == null)
                {
                    error = "конфиг пуст или не разобран";
                    return CreateDefault();
                }

                cfg.Validate(ref error);
                return cfg;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return CreateDefault();
            }
        }

        public void Validate(ref string error)
        {
            var problems = new List<string>();

            if (AreaBudgetPerPlayer <= 0) { problems.Add("AreaBudgetPerPlayer <= 0, взято 40000"); AreaBudgetPerPlayer = 40000; }
            if (MinRegionSize <= 0) { problems.Add("MinRegionSize <= 0, взято 10"); MinRegionSize = 10; }
            if (MaxRegionSize <= 0) { problems.Add("MaxRegionSize <= 0, взято 100"); MaxRegionSize = 100; }
            if (MaxRegionSize < MinRegionSize)
            {
                problems.Add("MaxRegionSize < MinRegionSize, выровнено по MinRegionSize");
                MaxRegionSize = MinRegionSize;
            }
            if (SpawnProtectionRadius < 0) { problems.Add("SpawnProtectionRadius < 0, взято 0"); SpawnProtectionRadius = 0; }
            if (HighlightRadius <= 0) { problems.Add("HighlightRadius <= 0, взято 100"); HighlightRadius = 100; }
            if (HighlightRefreshSeconds <= 0) { problems.Add("HighlightRefreshSeconds <= 0, взято 4"); HighlightRefreshSeconds = 4; }
            if (HighlightChunkSize <= 0 || HighlightChunkSize > 200)
            {
                problems.Add("HighlightChunkSize вне диапазона 1..200, взято 50");
                HighlightChunkSize = 50;
            }
            if (MaxRegionNameLength <= 0) { problems.Add("MaxRegionNameLength <= 0, взято 32"); MaxRegionNameLength = 32; }
            if (HighlightAirWall <= 0 || HighlightAirWall >= WallID.Count)
            {
                problems.Add("HighlightAirWall вне диапазона id стен, взята стеклянная стена");
                HighlightAirWall = WallID.Glass;
            }

            if (Messages == null)
            {
                problems.Add("отсутствует секция Messages, взяты тексты по умолчанию");
                Messages = new SimpleRegionsMessages();
            }

            if (problems.Count > 0)
                error = string.Join("; ", problems);
        }

        public void Write(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented), Encoding.UTF8);
        }
    }
}
