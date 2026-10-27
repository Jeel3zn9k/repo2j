// ============================================================
// ФАЙЛ: TabloPatch.cs  (версия под .NET Framework 3.5, C# 3.0/4.0)
// Класть целиком в папку проекта Tablo, рядом с frmMain.cs
// ============================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows.Forms;

namespace TabloPatch
{
    public static class TimeFmt
    {
        public static string Fmt(int totalSec)
        {
            if (totalSec < 0) totalSec = 0;
            return string.Format("{0:D2}:{1:D2}", totalSec / 60, totalSec % 60);
        }

        public static bool TryParse(string s, out int sec)
        {
            sec = 0;
            if (s == null) return false;
            string t = s.Trim();
            if (t.Length == 0) return false;
            string[] p = t.Split(':');
            int m, ss;
            if (p.Length == 2 && int.TryParse(p[0], out m) && int.TryParse(p[1], out ss))
            { sec = m * 60 + ss; return true; }
            if (p.Length == 1 && int.TryParse(p[0], out m))
            { sec = m * 60; return true; }
            return false;
        }
    }

    public class PenaltyInfo
    {
        public bool HomeTeam;
        public int PlayerNumber;
        public int SecondsTotal;
        public int SecondsLeft;

        public override string ToString()
        {
            return (HomeTeam ? "ХОЗ" : "ГОС") + " №" + PlayerNumber + " " + TimeFmt.Fmt(SecondsLeft);
        }
    }

    public class GameClockCore
    {
        // существующие настройки программы
        public int PeriodTimeSec { get; set; }
        public int OvertimeTimeSec { get; set; }
        public bool MainClockCountDown { get; set; }    // ФУНКЦИЯ 1: по умолчанию обратный ход
        public bool PrestartCountDown { get; set; }     // БАГ 3: по умолчанию обратный ход

        // НОВЫЕ ЭТАЛОН-ПЕРЕМЕННЫЕ (защита времени)
        private int savedMainPeriodTimeSec = -1;        // переменная 1: основное время
        private int savedOvertimeTimeSec = -1;          // переменная 2: овертайм
        public int SavedMainPeriodTimeSec { get { return savedMainPeriodTimeSec; } }
        public int SavedOvertimeTimeSec { get { return savedOvertimeTimeSec; } }
        public bool MainTimeMemoryLocked { get; private set; }
        public bool OtTimeMemoryLocked { get; private set; }

        private readonly Dictionary<int, int> periodInitialTimeSec = new Dictionary<int, int>();
        private bool periodTimeWasCorrected;
        private int periodEndRestoreDoneFor = -1;       // восстановление ОДИН раз в конце периода

        public int CurrentPeriod { get; private set; }
        public int ClockDisplaySec { get; private set; }
        public int CurrentPeriodLengthSec { get; private set; }
        public bool Running { get; private set; }
        public int HomeScore { get; private set; }
        public int AwayScore { get; private set; }
        private readonly List<PenaltyInfo> penalties = new List<PenaltyInfo>();
        public List<PenaltyInfo> Penalties { get { return penalties; } }

        public GameClockCore()
        {
            CurrentPeriod = 1;
            PeriodTimeSec = 20 * 60;
            OvertimeTimeSec = 5 * 60;
            MainClockCountDown = true;
            PrestartCountDown = true;
            CurrentPeriodLengthSec = PeriodTimeSec;
            ClockDisplaySec = PeriodTimeSec;
        }

        public bool IsOvertime { get { return CurrentPeriod >= 4; } }
        public bool IsCountDownNow { get { return IsOvertime ? true : MainClockCountDown; } }

        public int DomainSettingSec
        {
            get { return IsOvertime ? OvertimeTimeSec : PeriodTimeSec; }
            set { if (IsOvertime) OvertimeTimeSec = value; else PeriodTimeSec = value; }
        }
        public int DomainSavedSec { get { return IsOvertime ? savedOvertimeTimeSec : savedMainPeriodTimeSec; } }

        public event Action<string> Log;
        public event Action StateChanged;
        private void Raise() { if (StateChanged != null) StateChanged(); }
        private void LogIt(string s) { if (Log != null) Log(s); }

        // кнопка настроек "Запомнить время периодов"
        public void RememberPeriodTimesFromSettings()
        {
            if (MainTimeMemoryLocked && OtTimeMemoryLocked)
            {
                LogIt("ЗАПОМИНАНИЕ: эталон уже запомнен. Разблокировка - мастер-сброс или ручная правка эталона.");
                return;
            }
            savedMainPeriodTimeSec = PeriodTimeSec; MainTimeMemoryLocked = true;
            savedOvertimeTimeSec = OvertimeTimeSec; OtTimeMemoryLocked = true;
            LogIt("ЗАПОМИНАНИЕ ЭТАЛОНА: осн.=" + TimeFmt.Fmt(savedMainPeriodTimeSec) +
                  ", овертайм=" + TimeFmt.Fmt(savedOvertimeTimeSec) + " (заблокировано)");
            Raise();
        }

        // аварийная ручная правка эталона (только из отдельных полей настроек)
        public void ManualEditSavedMainTime(int sec)
        {
            savedMainPeriodTimeSec = sec; MainTimeMemoryLocked = true;
            LogIt("РУЧНАЯ ПРАВКА ЭТАЛОНА осн.: " + TimeFmt.Fmt(sec)); Raise();
        }
        public void ManualEditSavedOtTime(int sec)
        {
            savedOvertimeTimeSec = sec; OtTimeMemoryLocked = true;
            LogIt("РУЧНАЯ ПРАВКА ЭТАЛОНА овертайма: " + TimeFmt.Fmt(sec)); Raise();
        }

        private void EnsureMemoryLocked()
        {
            // триггер: первый старт периода сам запоминает эталон
            if (IsOvertime)
            {
                if (savedOvertimeTimeSec < 0)
                {
                    savedOvertimeTimeSec = OvertimeTimeSec; OtTimeMemoryLocked = true;
                    LogIt("ЗАПОМИНАНИЕ ПРИ СТАРТЕ: овертайм=" + TimeFmt.Fmt(savedOvertimeTimeSec));
                }
            }
            else
            {
                if (savedMainPeriodTimeSec < 0)
                {
                    savedMainPeriodTimeSec = PeriodTimeSec; MainTimeMemoryLocked = true;
                    LogIt("ЗАПОМИНАНИЕ ПРИ СТАРТЕ: осн.=" + TimeFmt.Fmt(savedMainPeriodTimeSec));
                }
            }
        }

        private void PrepareClock()
        {
            EnsureMemoryLocked();
            int len;
            if (!periodInitialTimeSec.TryGetValue(CurrentPeriod, out len))
            { len = DomainSettingSec; periodInitialTimeSec[CurrentPeriod] = len; }
            CurrentPeriodLengthSec = len;
            ClockDisplaySec = IsCountDownNow ? len : 0;
            periodTimeWasCorrected = false;
            periodEndRestoreDoneFor = -1;
        }

        public void StartClock()
        {
            PrepareClock(); Running = true;
            LogIt("СТАРТ периода " + CurrentPeriod + ": табло=" + TimeFmt.Fmt(ClockDisplaySec) +
                  ", ход=" + (IsCountDownNow ? "обратный" : "прямой"));
            Raise();
        }

        public void StopClock()
        {
            Running = false;
            LogIt("СТОП времени, на табло " + TimeFmt.Fmt(ClockDisplaySec));
            Raise();
        }

        public void Tick1Sec()
        {
            if (!Running) return;
            if (IsCountDownNow) { if (ClockDisplaySec > 0) ClockDisplaySec--; }
            else { if (ClockDisplaySec < CurrentPeriodLengthSec) ClockDisplaySec++; }

            for (int i = penalties.Count - 1; i >= 0; i--)
            {
                PenaltyInfo p = penalties[i];
                if (p.SecondsLeft > 0) p.SecondsLeft--;
                if (p.SecondsLeft == 0)
                {
                    penalties.RemoveAt(i);
                    LogIt("ШТРАФ ИСТЁК: " + p.ToString() + " убран с табло");
                }
            }

            bool periodEnd = IsCountDownNow ? (ClockDisplaySec == 0)
                                            : (ClockDisplaySec >= CurrentPeriodLengthSec);
            if (periodEnd) { Running = false; OnPeriodEnd(); }
            Raise();
        }

        public void PeriodNext()
        {
            OnPeriodEnd();
            if (CurrentPeriod < 9) CurrentPeriod++;
            PrepareClock();
            LogIt("ПЕРИОД -> " + CurrentPeriod + (IsOvertime ? " (овертайм)" : ""));
            Raise();
        }

        public void PeriodPrev()
        {
            if (CurrentPeriod > 1) CurrentPeriod--;
            PrepareClock();
            LogIt("ПЕРИОД -> " + CurrentPeriod);
            Raise();
        }
              public void SetCurrentPeriod(int period)
        {
            if (period != CurrentPeriod)
            {
                periodEndRestoreDoneFor = -1;
            }
            CurrentPeriod = period;
        }


        // конец периода: сверка настройки с эталоном, восстановление ОДИН раз
        public void OnPeriodEnd()
        {
            if (periodEndRestoreDoneFor == CurrentPeriod) return;
            periodEndRestoreDoneFor = CurrentPeriod;
            Running = false;
            int saved = DomainSavedSec;
            if (periodTimeWasCorrected && saved > 0 && DomainSettingSec != saved)
            {
                string broken = TimeFmt.Fmt(DomainSettingSec);
                DomainSettingSec = saved;
                LogIt("КОНЕЦ периода " + CurrentPeriod + ": ВРЕМЯ НЕ СОВПАДАЛО (было " + broken +
                      ") -> восстановлено из эталона " + TimeFmt.Fmt(saved));
            }
            else
            {
                LogIt("КОНЕЦ периода " + CurrentPeriod + ": настройка и эталон совпадают");
            }
            periodTimeWasCorrected = false;
            Raise();
        }

        // БАГ 4 + ФУНКЦИЯ 3: корректировка применяется СРАЗУ и ТОЧНО, эталон не трогается
        public void ApplyManualTimeCorrection(int sec)
        {
            if (sec < 0) sec = 0;
            ClockDisplaySec = sec;
            DomainSettingSec = sec;
            periodTimeWasCorrected = true;
            periodEndRestoreDoneFor = -1;
            LogIt("КОРРЕКТИРОВКА: выставлено ТОЧНО " + TimeFmt.Fmt(sec) + " (без вычитания прошедшего)");
            Raise();
        }

        // кнопка "Сброс времени" оставлена рабочей: возврат к эталону
        public void ResetClockToSavedTime()
        {
            int saved = DomainSavedSec;
            ClockDisplaySec = (saved > 0) ? saved : DomainSettingSec;
            CurrentPeriodLengthSec = ClockDisplaySec;
            Running = false;
            LogIt("СБРОС ВРЕМЕНИ: возвращено к эталону " + TimeFmt.Fmt(ClockDisplaySec));
            Raise();
        }

        // БАГИ 1 и 2: единый путь удаления для периодов 1..9
        public void AddPenalty(bool home, int playerNumber, int minutes)
        {
            int sec = minutes * 60;
            PenaltyInfo existing = null;
            for (int i = 0; i < penalties.Count; i++)
            {
                if (penalties[i].HomeTeam == home && penalties[i].PlayerNumber == playerNumber)
                { existing = penalties[i]; break; }
            }
            if (existing != null)
            {
                existing.SecondsTotal = sec; existing.SecondsLeft = sec;
                LogIt("УДАЛЕНИЕ ОБНОВЛЕНО: " + existing.ToString());
            }
            else
            {
                PenaltyInfo p = new PenaltyInfo();
                p.HomeTeam = home; p.PlayerNumber = playerNumber;
                p.SecondsTotal = sec; p.SecondsLeft = sec;
                penalties.Add(p);
                LogIt("УДАЛЕНИЕ ДОБАВЛЕНО: " + p.ToString() + " (период " + CurrentPeriod + ")");
            }
            Raise();
        }

        public void RemovePenalizedPlayer(int playerNumber)
        {
            int before = penalties.Count;
            for (int i = penalties.Count - 1; i >= 0; i--)
            {
                if (penalties[i].PlayerNumber == playerNumber) penalties.RemoveAt(i);
            }
            if (penalties.Count < before)
                LogIt("УДАЛЕНИЕ СНЯТО: игрок №" + playerNumber + " убран с табло немедленно (период " + CurrentPeriod + ")");
            else
                LogIt("УДАЛЕНИЕ СНЯТО: игрок №" + playerNumber + " не найден среди удалённых");
            Raise();
        }

        public void ChangeScore(bool home, int delta)
        {
            if (home) HomeScore = Math.Max(0, HomeScore + delta);
            else AwayScore = Math.Max(0, AwayScore + delta);
            LogIt("СЧЁТ: " + HomeScore + ":" + AwayScore);
            Raise();
        }

        // МАСТЕР-СБРОС: единственная полная разблокировка эталонов
        public void MasterReset()
        {
            Running = false;
            CurrentPeriod = 1;
            HomeScore = 0; AwayScore = 0;
            penalties.Clear();
            savedMainPeriodTimeSec = -1; savedOvertimeTimeSec = -1;
            MainTimeMemoryLocked = false; OtTimeMemoryLocked = false;
            periodInitialTimeSec.Clear();
            periodTimeWasCorrected = false;
            periodEndRestoreDoneFor = -1;
            CurrentPeriodLengthSec = PeriodTimeSec;
            ClockDisplaySec = MainClockCountDown ? PeriodTimeSec : 0;
            LogIt("МАСТЕР-СБРОС: эталоны разблокированы и очищены");
            Raise();
        }
    }

    // БАГ 3: прямой ход заставки реально идёт вверх (00:00 -> total)
    public static class SplashClock
    {
        public static int DisplaySec(int elapsedSec, int totalSec, bool countDown)
        {
            if (countDown)
            {
                int v = totalSec - elapsedSec;
                return v < 0 ? 0 : v;
            }
            return elapsedSec > totalSec ? totalSec : elapsedSec;
        }
    }

    public static class SplashFormPatch
    {
        public static string PrestartDisplay(int elapsedSec, int totalSec, bool countDown)
        {
            return TimeFmt.Fmt(SplashClock.DisplaySec(elapsedSec, totalSec, countDown));
        }
    }

    public enum RemoteCmd
    {
        Start, Stop, PeriodNext, PeriodPrev,
        TimeSetSave, TimeResetToSaved,
        PenaltyAdd, PenaltyRemovePlayer,
        ScoreHomePlus, ScoreHomeMinus, ScoreAwayPlus, ScoreAwayMinus,
        MasterReset, SplashShow, SplashHide,
        TimeoutRequest, AdvertPause, Intermission,   // недоделаны: безопасный no-op
        TimeoutHome, TimeoutAway,
        CounterSet, ScoreSet, WhistleBeep, WhistleStart, WhistleStop,
        BreakTimeSet, TimeoutTimeSet, ScriptPlay, ScriptStop,
        SetPeriodValue, TimeResetOnly, DelGamerPacket
    }

    public struct RemoteCommand
    {
        public RemoteCmd Cmd;
        public int TimeSec;
        public bool HomeTeam;
        public int PlayerNumber;
        public int Minutes;

        public string Describe()
        {
            switch (Cmd)
            {
                case RemoteCmd.Start: return "СТАРТ времени";
                case RemoteCmd.Stop: return "СТОП времени";
                case RemoteCmd.PeriodNext: return "ПЕРИОД +1";
                case RemoteCmd.PeriodPrev: return "ПЕРИОД -1";
                case RemoteCmd.TimeSetSave: return "ВРЕМЯ ВЫСТАВИТЬ+СОХРАНИТЬ " + TimeFmt.Fmt(TimeSec);
                case RemoteCmd.TimeResetToSaved: return "СБРОС ВРЕМЕНИ к эталону";
                case RemoteCmd.PenaltyAdd: return "УДАЛЕНИЕ " + (HomeTeam ? "ХОЗ" : "ГОС") + " №" + PlayerNumber + " " + Minutes + " мин";
                case RemoteCmd.PenaltyRemovePlayer: return "СНЯТЬ УДАЛЕНИЕ игрока №" + PlayerNumber;
                case RemoteCmd.ScoreHomePlus: return "ГОЛ хозяева +1";
                case RemoteCmd.ScoreHomeMinus: return "ХОЗЯЕВА -1";
                case RemoteCmd.ScoreAwayPlus: return "ГОЛ гости +1";
                case RemoteCmd.ScoreAwayMinus: return "ГОСТИ -1";
                case RemoteCmd.MasterReset: return "МАСТЕР-СБРОС";
                case RemoteCmd.SplashShow: return "ПОКАЗАТЬ ЗАСТАВКУ";
                case RemoteCmd.SplashHide: return "СКРЫТЬ ЗАСТАВКУ";
                case RemoteCmd.TimeoutRequest: return "ТАЙМАУТ (резерв)";
                case RemoteCmd.TimeoutHome: return "ТАЙМАУТ хозяева";
                case RemoteCmd.TimeoutAway: return "ТАЙМАУТ гости";
                case RemoteCmd.CounterSet: return "УСТАНОВИТЬ СЧЁТЧИК ВРЕМЕНИ (SetCounterGame) " + TimeFmt.Fmt(TimeSec);
                case RemoteCmd.ScoreSet: return "УСТАНОВИТЬ СЧЁТ (SetCount) " + PlayerNumber + ":" + Minutes;
                case RemoteCmd.WhistleBeep: return "СВИСТОК (звук)";
                case RemoteCmd.WhistleStart: return "СВИСТОК: старт (ChangeModeGame+Enable)";
                case RemoteCmd.WhistleStop: return "СВИСТОК: стоп (ChangeModeGame)";
                case RemoteCmd.BreakTimeSet: return "ВРЕМЯ ПЕРЕРЫВА (SetBreak) " + TimeSec + " сек";
                case RemoteCmd.TimeoutTimeSet: return "ВРЕМЯ ТАЙМАУТА (SetTimeout) " + TimeSec + " сек";
                case RemoteCmd.ScriptPlay: return "СЦЕНАРИЙ играть №" + PlayerNumber;
                case RemoteCmd.ScriptStop: return "СЦЕНАРИЙ стоп";
                case RemoteCmd.SetPeriodValue: return "УСТАНОВИТЬ ПЕРИОД (ResetMatch.SetPeriod) " + PlayerNumber;
                case RemoteCmd.TimeResetOnly: return "СБРОС ВРЕМЕНИ (ResetMatch.ResetGameTime)";
                case RemoteCmd.DelGamerPacket: return "DELGAMER: №" + PlayerNumber + " время=" + Minutes + " мин команда=" + (HomeTeam ? "A" : "B");
                case RemoteCmd.AdvertPause: return "РЕКЛАМНАЯ ПАУЗА (резерв)";
                case RemoteCmd.Intermission: return "ПЕРЕРЫВ (резерв)";
            }
            return Cmd.ToString();
        }
    }

    // ================== БЛОК 4: ИНФРАСТРУКТУРА НАДЁЖНОСТИ ==================
    // F8-04/F8-06/F9-03: crash_log.txt с ротацией по поколениям (2 МБ x 3) и
    // запасным путём в %TEMP%, если папка программы недоступна на запись.
    // Метки времени — в инвариантной культуре (одинаковый вид на любой локали).
    public static class CrashLog
    {
        private static readonly object sync = new object();
        private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        private const long MaxBytes = 2 * 1024 * 1024;
        public const int Generations = 3;

        public static string MainPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt"); }
        }

        public static string TempPath
        {
            get { return Path.Combine(Path.GetTempPath(), "Tablo_crash_log.txt"); }
        }

        public static void Append(string tag, string msg)
        {
            lock (sync)
            {
                string line = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", Inv) + " " + tag + ": " + msg + "\r\n";
                if (!TryAppend(MainPath, line))
                {
                    // F8-06: папка программы не пишется (нет прав/UAC) — пишем в %TEMP%,
                    //диагностика не теряется.
                    TryAppend(TempPath, line);
                }
            }
        }

        private static bool TryAppend(string path, string line)
        {
            try
            {
                Rotate(path);
                File.AppendAllText(path, line, Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Rotate(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxBytes)
                {
                    return;
                }
                for (int g = Generations - 1; g >= 1; g--)
                {
                    string src = GenPath(path, g);
                    string dst = GenPath(path, g + 1);
                    if (File.Exists(src))
                    {
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(src, dst);
                    }
                }
                string first = GenPath(path, 1);
                if (File.Exists(first)) File.Delete(first);
                File.Move(path, first);
            }
            catch
            {
            }
        }

        public static string GenPath(string path, int gen)
        {
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path) + "." + gen.ToString(Inv) + Path.GetExtension(path);
            return string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
        }

        public static void ClearAll()
        {
            lock (sync)
            {
                try
                {
                    for (int g = 1; g <= Generations; g++)
                    {
                        string p = GenPath(MainPath, g);
                        if (File.Exists(p)) File.Delete(p);
                    }
                    if (File.Exists(MainPath)) File.Delete(MainPath);
                }
                catch
                {
                }
            }
        }
    }

    // F8-03: вместо пустых catch{} — считаемая диагностика. Первые 3 случая
    // каждой метки пишутся в crash_log.txt, далее только каждый 100-й (со счётчиком):
    // повторяющийся сбой не затирает лог, но остаётся видимым.
    public static class Diag
    {
        private static readonly object sync = new object();
        private static readonly System.Collections.Generic.Dictionary<string, int> counts = new System.Collections.Generic.Dictionary<string, int>();

        public static void Note(string tag, Exception ex)
        {
            int n;
            lock (sync)
            {
                if (!counts.TryGetValue(tag, out n)) n = 0;
                n++;
                counts[tag] = n;
            }
            if (n <= 3 || n % 100 == 0)
            {
                CrashLog.Append("DIAG", tag + " (случай " + n + ")" + ((ex != null) ? (": " + ex.Message) : ""));
            }
        }

        // Однократная запись в remote_com_log (для «шумных», но важных наблюдений).
        public static void LogOnce(string tag, string msg)
        {
            int n;
            lock (sync)
            {
                if (!counts.TryGetValue(tag, out n)) n = 0;
                n++;
                counts[tag] = n;
            }
            if (n == 1)
            {
                SerialPortLog.Write(msg);
            }
        }
    }

    // F4-03/F4-04: единый помощник чтения/записи текстовых файлов.
    // Запись — атомарная (tmp -> File.Replace), опционально с BOM UTF-8
    // (русский текст в Блокноте без «кракозябр»). Чтение — с определением
    // BOM/UTF-8/ANSI, чтобы старые файлы (Windows-1251) читались корректно.
    public static class TextIO
    {
        public static void WriteAll(string path, string text, bool withBom)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string tmp = path + ".tmp";
            Encoding enc = withBom ? new UTF8Encoding(true) : new UTF8Encoding(false);
            File.WriteAllText(tmp, text ?? "", enc);
            if (File.Exists(path))
            {
                bool replaced = false;
                try
                {
                    File.Replace(tmp, path, null);
                    replaced = true;
                }
                catch
                {
                }
                if (!replaced)
                {
                    File.Delete(path);
                    File.Move(tmp, path);
                }
                return;
            }
            File.Move(tmp, path);
        }

        public static string[] ReadAllLines(string path)
        {
            Encoding enc = DetectEncoding(path);
            return File.ReadAllLines(path, enc);
        }

        public static string ReadAllText(string path)
        {
            Encoding enc = DetectEncoding(path);
            return File.ReadAllText(path, enc);
        }

        private static Encoding DetectEncoding(string path)
        {
            try
            {
                byte[] all = File.ReadAllBytes(path);
                if (all.Length >= 3 && all[0] == 0xEF && all[1] == 0xBB && all[2] == 0xBF)
                {
                    return Encoding.UTF8;
                }
                UTF8Encoding strict = new UTF8Encoding(false, true);
                strict.GetString(all);   // исключение => это не UTF-8
                return Encoding.UTF8;
            }
            catch (ArgumentException)
            {
                return Encoding.Default;   // ANSI (на машине заказчика — Windows-1251)
            }
            catch
            {
                return Encoding.Default;
            }
        }
    }

    public static class SerialPortLog
    {
        private static readonly object sync = new object();
        private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        private static string filePath;
        public static bool Enabled = true;
        // БЛОК 4, F8-05: постоянно открытый писатель (AutoFlush).
        private static StreamWriter writer;
        // БЛОК 4, F8-04: ротация поколениями 2 МБ x 3.
        private const long MaxLogBytes = 2 * 1024 * 1024;
        private const int Generations = 3;
        // БЛОК 4, F5-10: счётчик одинаковых HEX-кадров подряд.
        private static string lastHexLine;
        private static int hexRepeat;

        public static void Init(string directory)
        {
            lock (sync)
            {
                filePath = Path.Combine(directory, "remote_com_log.txt");
                WriteCore("=== СЕССИЯ " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv) + " ===");
            }
        }

        // БЛОК 4, F8-05: лог пишется через постоянно открытый StreamWriter (AutoFlush)
        // вместо открытия/закрытия файла на каждую строку; Enabled проверяется ДО
        // захвата блокировки (выключенный лог не создаёт очередей на lock).
        // БЛОК 4, F8-04: вместо «оставить 10 строк» — ротация поколениями:
        // при достижении 2 МБ файл становится remote_com_log.1.txt, прежний .1 -> .2,
        // .2 -> .3 (всего 3 поколения, старое удаляется). История остаётся читаемой.
        public static void Write(string line)
        {
            if (!Enabled) return;
            lock (sync)
            {
                WriteCore(line);
            }
        }

        private static void WriteCore(string line)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                    filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "remote_com_log.txt");
                EnsureWriter();
                if (writer != null)
                    writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff", Inv) + "  " + line);
            }
            catch (Exception ex)
            {
                CloseWriter();
                CrashLog.Append("LOG", "не удалось записать remote_com_log.txt: " + ex.Message);
            }
        }

        // Путь, на который открыт текущий писатель — если filePath сменился
        // (Init вызван после фолбэк-записи), писатель переоткрывается.
        private static string writerPath;

        private static void EnsureWriter()
        {
            FileInfo fi = new FileInfo(filePath);
            if (fi.Exists && fi.Length >= MaxLogBytes)
            {
                Rotate();
            }
            if (writer != null && writerPath != filePath)
            {
                CloseWriter();
            }
            if (writer == null)
            {
                FileStream fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                writer = new StreamWriter(fs, new UTF8Encoding(false));
                writer.AutoFlush = true;
                writerPath = filePath;
            }
        }

        private static void Rotate()
        {
            try
            {
                CloseWriter();
                for (int g = Generations - 1; g >= 1; g--)
                {
                    string src = CrashLog.GenPath(filePath, g);
                    string dst = CrashLog.GenPath(filePath, g + 1);
                    if (File.Exists(src))
                    {
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(src, dst);
                    }
                }
                string first = CrashLog.GenPath(filePath, 1);
                if (File.Exists(first)) File.Delete(first);
                if (File.Exists(filePath)) File.Move(filePath, first);
            }
            catch (Exception ex)
            {
                CrashLog.Append("LOG", "ротация remote_com_log.txt не выполнена: " + ex.Message);
            }
        }

        private static void CloseWriter()
        {
            try
            {
                if (writer != null) writer.Close();
            }
            catch
            {
            }
            writer = null;
            writerPath = null;
        }

        // БЛОК 4, F5-10: идущие подряд одинаковые HEX-кадры не дублируются в лог —
        // пишется первый кадр, затем итог «всего N повторов» при смене кадра
        // (и контрольная отметка каждую 1000 повторов). Поток одинаковых кадров
        // пульта больше не раздувает лог до мегабайтов за минуту.
        public static void WriteHex(string prefix, byte[] data)
        {
            if (!Enabled) return;
            string line = prefix + ": " + BitConverter.ToString(data);
            lock (sync)
            {
                if (line == lastHexLine)
                {
                    hexRepeat++;
                    if (hexRepeat % 1000 == 0)
                        WriteCore(line + " — повтор №" + hexRepeat.ToString(Inv) + " (идёт поток одинаковых кадров)");
                    return;
                }
                if (hexRepeat > 1 && lastHexLine != null)
                    WriteCore(lastHexLine + " — всего повторов: " + hexRepeat.ToString(Inv));
                lastHexLine = line;
                hexRepeat = 1;
                WriteCore(line);
            }
        }

        public static void Close()
        {
            lock (sync)
            {
                CloseWriter();
            }
        }

			public static void ClearAll()
	{
		lock (sync)
		{
			CloseWriter();
			string[] names = new string[] { "remote_com_log.txt", "crash_log.txt" };
			for (int i = 0; i < names.Length; i++)
			{
				try
				{
					string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, names[i]);
					for (int g = 1; g <= CrashLog.Generations; g++)
					{
						string pg = CrashLog.GenPath(p, g);
						if (File.Exists(pg)) File.Delete(pg);
					}
					if (File.Exists(p))
					{
						File.Delete(p);
					}
				}
				catch (Exception ex)
				{
					Diag.Note("ClearAll логов", ex);
				}
			}
			lastHexLine = null;
			hexRepeat = 0;
			CrashLog.Append("LOG", "логи очищены вручную из режима разработчика");
		}
	}
    }

    // ================== ПУНКТ 3: ЖУРНАЛ НЕУДАЧНЫХ ПОПЫТОК ВХОДА ==================
    // Отдельный текстовый файл login_fails.txt в папке программы: ОДНА запись на строку,
    // состав строки — время, IP и имя устройства (User-Agent). Файл нужен, чтобы
    // оператор/инженер мог постфактум понять, кто и когда подбирал пароль.
    // Ротация: при превышении 1 МБ файл становится login_fails.1.txt (одно поколение) —
    // журнал не растёт бесконечно. Ошибки записи не влияют на работу сервера.
    public static class LoginFailLog
    {
        private static readonly object sync = new object();
        private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        private const long MaxBytes = 1024 * 1024;

        public static string MainPath
        {
            get
            {
                try { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "login_fails.txt"); }
                catch { return "login_fails.txt"; }
            }
        }

        public static void Write(string device, string ip)
        {
            lock (sync)
            {
                try
                {
                    string dev = (device == null) ? "" : device.Replace("\r", " ").Replace("\n", " ").Trim();
                    if (dev.Length == 0) { dev = "(имя устройства неизвестно)"; }
                    if (dev.Length > 200) { dev = dev.Substring(0, 200); }
                    string addr = (ip == null || ip.Length == 0) ? "-" : ip;
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv) + " | IP " + addr + " | " + dev;
                    string p = MainPath;
                    Rotate(p);
                    File.AppendAllText(p, line + "\r\n", Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    CrashLog.Append("LOGINFAIL", "не удалось записать login_fails.txt: " + ex.Message);
                }
            }
        }

        private static void Rotate(string p)
        {
            try
            {
                FileInfo fi = new FileInfo(p);
                if (!fi.Exists || fi.Length < MaxBytes) { return; }
                string dir = Path.GetDirectoryName(p);
                string gen = (string.IsNullOrEmpty(dir) ? "" : dir + Path.DirectorySeparatorChar) + "login_fails.1.txt";
                if (File.Exists(gen)) { File.Delete(gen); }
                File.Move(p, gen);
            }
            catch
            {
            }
        }
    }

    public interface ITabloView
    {
        void ShowSplash();
        void HideSplash();
    }

    public class RemoteCommandDispatcher
    {
        private readonly GameClockCore core;
        private readonly ITabloView view;
        public event Action<string> LogLine;
        private Action<RemoteCommand> _externalHandler;
        public void SetExternalHandler(Action<RemoteCommand> h)
        {
            _externalHandler = h;
        }
        public void LogPublic(string s) { L(s); }

        public RemoteCommandDispatcher(GameClockCore core, ITabloView view)
        {
            this.core = core;
            this.view = view;
        }

        private void L(string s)
        {
            SerialPortLog.Write(s);
            if (LogLine != null) LogLine(s);
        }

        public void ProcessRemoteCommand(RemoteCommand c)
        {
            L("КОМАНДА: " + c.Describe());
            if (_externalHandler != null)
            {
                _externalHandler(c);
                L("РЕЗУЛЬТАТ: команда обработана основной формой");
                return;
            }
            switch (c.Cmd)
            {
                case RemoteCmd.Start: core.StartClock(); break;
                case RemoteCmd.Stop: core.StopClock(); break;
                case RemoteCmd.PeriodNext: core.PeriodNext(); break;
                case RemoteCmd.PeriodPrev: core.PeriodPrev(); break;
                case RemoteCmd.TimeSetSave: core.ApplyManualTimeCorrection(c.TimeSec); break;
                case RemoteCmd.TimeResetToSaved: core.ResetClockToSavedTime(); break;
                case RemoteCmd.PenaltyAdd: core.AddPenalty(c.HomeTeam, c.PlayerNumber, c.Minutes); break;
                case RemoteCmd.PenaltyRemovePlayer: core.RemovePenalizedPlayer(c.PlayerNumber); break;
                case RemoteCmd.ScoreHomePlus: core.ChangeScore(true, 1); break;
                case RemoteCmd.ScoreHomeMinus: core.ChangeScore(true, -1); break;
                case RemoteCmd.ScoreAwayPlus: core.ChangeScore(false, 1); break;
                case RemoteCmd.ScoreAwayMinus: core.ChangeScore(false, -1); break;
                case RemoteCmd.MasterReset: core.MasterReset(); break;
                case RemoteCmd.SplashShow: if (view != null) view.ShowSplash(); break;
                case RemoteCmd.SplashHide: if (view != null) view.HideSplash(); break;
                case RemoteCmd.TimeoutRequest:
                case RemoteCmd.AdvertPause:
                case RemoteCmd.Intermission:
                    L("РЕЗЕРВ: функция не реализована, команда проигнорирована БЕЗ отказа программы");
                    break;
            }
            L("РЕЗУЛЬТАТ: период=" + core.CurrentPeriod + " время=" + TimeFmt.Fmt(core.ClockDisplaySec) +
   " счёт=" + core.HomeScore + ":" + core.AwayScore + " удалений=" + core.Penalties.Count);
        }

    }

    public static class PatchEntry
    {
        public static GameClockCore Core = new GameClockCore();
        private static RemoteCommandDispatcher _dispatcher;
        private static bool _logWired;
        private static bool _wired;

        public static RemoteCommandDispatcher Dispatcher
        {
            get
            {
                if (_dispatcher == null)
                {
                    EnsureLog();
                    _dispatcher = new RemoteCommandDispatcher(Core, null);
                }
                return _dispatcher;
            }
        }

        public static void Init(ITabloView view)
        {
            EnsureLog();
            _dispatcher = new RemoteCommandDispatcher(Core, view);
            _wired = false;
        }

        private static void EnsureLog()
        {
            if (_logWired)
            {
                return;
            }
            _logWired = true;
            SerialPortLog.Init(AppDomain.CurrentDomain.BaseDirectory);
            Core.Log += SerialPortLog.Write;
        }

        public static void OpenEmulator(Form owner)
        {
            WireExternalHandler(owner);
            RemoteEmulatorForm f = new RemoteEmulatorForm(Dispatcher, Core);
            f.Show(owner);
        }

        private static void WireExternalHandler(Form mainForm)
        {
            if (_wired || mainForm == null)
            {
                return;
            }
            try
            {
                System.Reflection.MethodInfo mi = mainForm.GetType().GetMethod("Patch_DispatchRemoteCommand",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (mi != null)
                {
                    Delegate d = Delegate.CreateDelegate(typeof(Action<RemoteCommand>), mainForm, mi);
                    Dispatcher.SetExternalHandler((Action<RemoteCommand>)d);
                    _wired = true;
                    Dispatcher.LogPublic("ЭМУЛЯТОР: подключен к обработчикам основной формы");
                }
            }
            catch
            {
            }
        }
    }
    public class TimeMemorySettingsControl : UserControl
    {
        private readonly GameClockCore core;
        private TextBox tbSavedMain;
        private TextBox tbSavedOt;
        private Label lblLock;

        public TimeMemorySettingsControl(GameClockCore core)
        {
            this.core = core;
            Width = 470; Height = 80;
            GroupBox g = new GroupBox();
            g.Text = "Запомненное (эталонное) время периодов - защищено от корректировок";
            g.Dock = DockStyle.Fill;
            Controls.Add(g);

            Label l1 = new Label(); l1.Text = "Эталон осн.:"; l1.Left = 10; l1.Top = 20; l1.Width = 75;
            g.Controls.Add(l1);
            tbSavedMain = new TextBox(); tbSavedMain.Left = 88; tbSavedMain.Top = 17; tbSavedMain.Width = 55;
            g.Controls.Add(tbSavedMain);

            Label l2 = new Label(); l2.Text = "Эталон овт:"; l2.Left = 150; l2.Top = 20; l2.Width = 70;
            g.Controls.Add(l2);
            tbSavedOt = new TextBox(); tbSavedOt.Left = 222; tbSavedOt.Top = 17; tbSavedOt.Width = 55;
            g.Controls.Add(tbSavedOt);

            Button btnRemember = new Button();
            btnRemember.Text = "Запомнить";
            btnRemember.Left = 285; btnRemember.Top = 15; btnRemember.Width = 80;
            btnRemember.Click += delegate { core.RememberPeriodTimesFromSettings(); };
            g.Controls.Add(btnRemember);

            Button btnManual = new Button();
            btnManual.Text = "Правка";
            btnManual.Left = 370; btnManual.Top = 15; btnManual.Width = 60;
            btnManual.Click += delegate
{
    int a, b;
    bool okA = TimeFmt.TryParse(tbSavedMain.Text, out a);
    bool okB = TimeFmt.TryParse(tbSavedOt.Text, out b);
    if (okA) core.ManualEditSavedMainTime(a);
    if (okB) core.ManualEditSavedOtTime(b);
};
            g.Controls.Add(btnManual);

            lblLock = new Label(); lblLock.Text = ""; lblLock.Left = 10; lblLock.Top = 48; lblLock.Width = 450;
            g.Controls.Add(lblLock);

            core.StateChanged += RefreshView;
            RefreshView();
        }

        private void RefreshView()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)delegate { RefreshView(); }); return; }
            tbSavedMain.Text = core.SavedMainPeriodTimeSec >= 0 ? TimeFmt.Fmt(core.SavedMainPeriodTimeSec) : "";
            tbSavedOt.Text = core.SavedOvertimeTimeSec >= 0 ? TimeFmt.Fmt(core.SavedOvertimeTimeSec) : "";
            lblLock.Text = "Блокировка: осн.=" + (core.MainTimeMemoryLocked ? "ДА" : "НЕТ") +
                           ", овт=" + (core.OtTimeMemoryLocked ? "ДА" : "НЕТ") +
                           ". Снятие: мастер-сброс или кнопка Правка.";
        }
    }


      	public class RemoteEmulatorForm : Form
	{
		private readonly RemoteCommandDispatcher dispatcher;
		private readonly GameClockCore core;
		private TextBox txtTime;
		private ComboBox cboTeam;
		private NumericUpDown nudPlayer;
		private NumericUpDown nudMinutes;
		private NumericUpDown nudScoreH;
		private NumericUpDown nudScoreA;
		private NumericUpDown nudPeriodSet;
		private NumericUpDown nudTimeoutSec;
		private NumericUpDown nudBreakSec;
		private NumericUpDown nudScript;
		private TextBox logBox;
		private Label extValue;
		private Label extState;
		private Label extFrame;

		public RemoteEmulatorForm(RemoteCommandDispatcher dispatcher, GameClockCore core)
		{
			this.dispatcher = dispatcher;
			this.core = core;
			Text = "Эмулятор пульта (команды 1 в 1 как физический пульт)";
			Size = new Size(780, 810);
			StartPosition = FormStartPosition.CenterParent;
			BuildUi();
			dispatcher.LogLine += AppendLog;
			core.Log += AppendLog;
			ExtOut.UiPush += this.OnExtPush;
			this.FormClosed += delegate
			{
				dispatcher.LogLine -= AppendLog;
				core.Log -= AppendLog;
				ExtOut.UiPush -= this.OnExtPush;
			};
		}

		private void AddBtn(Control parent, string text, int x, int y, int w, EventHandler h)
		{
			Button b = new Button();
			b.Text = text; b.Left = x; b.Top = y; b.Width = w;
			b.Click += h;
			parent.Controls.Add(b);
		}

		private NumericUpDown AddNum(Control parent, int x, int y, int w, decimal min, decimal max, decimal val)
		{
			NumericUpDown n = new NumericUpDown();
			n.Left = x; n.Top = y; n.Width = w; n.Minimum = min; n.Maximum = max; n.Value = val;
			parent.Controls.Add(n);
			return n;
		}

		private void Send(RemoteCmd cmd)
		{
			RemoteCommand c = new RemoteCommand(); c.Cmd = cmd;
			dispatcher.ProcessRemoteCommand(c);
		}

		private void SendRaw(RemoteCommand c)
		{
			dispatcher.ProcessRemoteCommand(c);
		}

		private void OnExtPush(string line, string svc)
		{
			if (IsDisposed) return;
			if (InvokeRequired) { try { BeginInvoke(new Action<string, string>(OnExtPush), new object[] { line, svc }); } catch { } return; }
			if (svc != null)
			{
				extFrame.Text = "СЛУЖЕБНОЕ: " + svc;
			}
			else if (line != null)
			{
				string[] p = line.TrimEnd(new char[] { '\r' }).Split(new char[] { ';' });
				if (p.Length >= 5)
				{
					extValue.Text = p[2];
					extState.Text = "РЕЖИМ: " + p[1] + " | СИГНАЛ: " + ((p[3] == "1") ? ("ДА -> " + p[4]) : "нет");
					extFrame.Text = "КАДР: " + line.TrimEnd(new char[] { '\r' });
				}
			}
		}

		private void BuildUi()
		{
			GroupBox g1 = new GroupBox(); g1.Text = "Игра (пульт: смена режима через свисток)"; g1.Left = 10; g1.Top = 10; g1.Width = 360; g1.Height = 120;
			Controls.Add(g1);
			AddBtn(g1, "СТАРТ", 10, 22, 105, delegate { Send(RemoteCmd.Start); });
			AddBtn(g1, "СТОП", 120, 22, 105, delegate { Send(RemoteCmd.Stop); });
			AddBtn(g1, "СВИСТОК звук", 230, 22, 120, delegate { Send(RemoteCmd.WhistleBeep); });
			AddBtn(g1, "ПОКАЗАТЬ ЗАСТАВКУ", 10, 55, 165, delegate { Send(RemoteCmd.SplashShow); });
			AddBtn(g1, "СКРЫТЬ ЗАСТАВКУ", 180, 55, 170, delegate { Send(RemoteCmd.SplashHide); });
			AddBtn(g1, "МАСТЕР-СБРОС", 10, 88, 340, delegate { Send(RemoteCmd.MasterReset); });
			GroupBox g2 = new GroupBox(); g2.Text = "Время (пульт: SetTimerGame / SetCounterGame / Reset)"; g2.Left = 380; g2.Top = 10; g2.Width = 370; g2.Height = 120;
			Controls.Add(g2);
			Label lt = new Label(); lt.Text = "MM:SS"; lt.Left = 10; lt.Top = 26; lt.Width = 42;
			g2.Controls.Add(lt);
			txtTime = new TextBox(); txtTime.Text = "13:27"; txtTime.Left = 55; txtTime.Top = 22; txtTime.Width = 70;
			g2.Controls.Add(txtTime);
			AddBtn(g2, "СОХРАНИТЬ ВРЕМЯ (SetTimerGame)", 130, 21, 230, delegate
			{
				int sec; if (!TimeFmt.TryParse(txtTime.Text, out sec)) { AppendLog("ОШИБКА ВВОДА: MM:SS"); return; }
				RemoteCommand c = new RemoteCommand(); c.Cmd = RemoteCmd.TimeSetSave; c.TimeSec = sec; SendRaw(c);
			});
			AddBtn(g2, "СБРОС ВРЕМЕНИ к эталону (панель)", 10, 55, 200, delegate { Send(RemoteCmd.TimeResetToSaved); });
			nudPeriodSet = AddNum(g2, 215, 55, 50, 1, 9, 1);
			AddBtn(g2, "ПЕРИОД = (пульт)", 270, 53, 90, delegate
			{
				RemoteCommand c = new RemoteCommand(); c.Cmd = RemoteCmd.SetPeriodValue; c.PlayerNumber = (int)nudPeriodSet.Value; SendRaw(c);
			});
			GroupBox g3 = new GroupBox(); g3.Text = "Удаления (пульт: DelGamer номер+время)"; g3.Left = 10; g3.Top = 140; g3.Width = 740; g3.Height = 60;
			Controls.Add(g3);
			cboTeam = new ComboBox(); cboTeam.DropDownStyle = ComboBoxStyle.DropDownList; cboTeam.Left = 10; cboTeam.Top = 22; cboTeam.Width = 90;
			cboTeam.Items.AddRange(new object[] { "ХОЗЯЕВА", "ГОСТИ" }); cboTeam.SelectedIndex = 0;
			g3.Controls.Add(cboTeam);
			nudPlayer = AddNum(g3, 105, 22, 55, 0, 99, 4);
			nudMinutes = AddNum(g3, 165, 22, 55, 0, 25, 2);
			AddBtn(g3, "DelGamer: номер+время", 230, 20, 160, delegate
			{
				RemoteCommand c = new RemoteCommand(); c.Cmd = RemoteCmd.DelGamerPacket;
				c.HomeTeam = cboTeam.SelectedIndex == 0; c.PlayerNumber = (int)nudPlayer.Value; c.Minutes = (int)nudMinutes.Value; SendRaw(c);
			});
			AddBtn(g3, "DelGamer: номер, Time=0", 395, 20, 160, delegate
			{
				RemoteCommand c = new RemoteCommand(); c.Cmd = RemoteCmd.DelGamerPacket;
				c.HomeTeam = cboTeam.SelectedIndex == 0; c.PlayerNumber = (int)nudPlayer.Value; c.Minutes = 0; SendRaw(c);
			});
			AddBtn(g3, "СНЯТЬ УДАЛЕНИЕ (панель)", 560, 20, 170, delegate
			{
				RemoteCommand c = new RemoteCommand(); c.Cmd = RemoteCmd.PenaltyRemovePlayer; c.PlayerNumber = (int)nudPlayer.Value; SendRaw(c);
			});
			GroupBox g4 = new GroupBox(); g4.Text = "Счёт (пульт: SetCount абсолютные значения)"; g4.Left = 10; g4.Top = 210; g4.Width = 360; g4.Height = 90;
			Controls.Add(g4);
			Label lh = new Label(); lh.Text = "Хозяева:"; lh.Left = 10; lh.Top = 26; lh.Width = 60; g4.Controls.Add(lh);
			nudScoreH = AddNum(g4, 72, 22, 50, 0, 99, 0);
			Label la = new Label(); la.Text = "Гости:"; la.Left = 130; la.Top = 26; la.Width = 45; g4.Controls.Add(la);
			nudScoreA = AddNum(g4, 175, 22, 50, 0, 99, 0);
			AddBtn(g4, "УСТАНОВИТЬ СЧЁТ", 235, 20, 115, delegate
			{
				RemoteCommand c = new RemoteCommand(); c.Cmd = RemoteCmd.ScoreSet; c.PlayerNumber = (int)nudScoreH.Value; c.Minutes = (int)nudScoreA.Value; SendRaw(c);
			});
			AddBtn(g4, "Хоз +1", 10, 55, 80, delegate { Send(RemoteCmd.ScoreHomePlus); });
			AddBtn(g4, "Хоз -1", 95, 55, 80, delegate { Send(RemoteCmd.ScoreHomeMinus); });
			AddBtn(g4, "Гост +1", 180, 55, 80, delegate { Send(RemoteCmd.ScoreAwayPlus); });
			AddBtn(g4, "Гост -1", 265, 55, 85, delegate { Send(RemoteCmd.ScoreAwayMinus); });
			GroupBox g5 = new GroupBox(); g5.Text = "Таймауты/перерыв"; g5.Left = 380; g5.Top = 210; g5.Width = 370; g5.Height = 90;
			Controls.Add(g5);
			AddBtn(g5, "ТАЙМАУТ Хоз.", 10, 20, 100, delegate { Send(RemoteCmd.TimeoutHome); });
			AddBtn(g5, "ТАЙМАУТ Гост.", 115, 20, 100, delegate { Send(RemoteCmd.TimeoutAway); });
			AddBtn(g5, "ПЕРЕРЫВ", 220, 20, 140, delegate { Send(RemoteCmd.Intermission); });
			nudTimeoutSec = AddNum(g5, 10, 55, 60, 0, 600, 30);
			AddBtn(g5, "Время таймаута, сек", 75, 53, 130, delegate
			{
				RemoteCommand c = new RemoteCommand(); c.Cmd = RemoteCmd.TimeoutTimeSet; c.TimeSec = (int)nudTimeoutSec.Value; SendRaw(c);
			});
			nudBreakSec = AddNum(g5, 210, 55, 60, 0, 600, 10);
			AddBtn(g5, "Время перерыва, сек", 275, 53, 90, delegate
			{
				RemoteCommand c = new RemoteCommand(); c.Cmd = RemoteCmd.BreakTimeSet; c.TimeSec = (int)nudBreakSec.Value; SendRaw(c);
			});
			GroupBox g6 = new GroupBox(); g6.Text = "Сценарии (пульт: SetScript) и сбросы (флаги ResetMatch)"; g6.Left = 10; g6.Top = 310; g6.Width = 740; g6.Height = 60;
			Controls.Add(g6);
			nudScript = AddNum(g6, 10, 22, 50, 0, 99, 1);
			AddBtn(g6, "ИГРАТЬ СЦЕНАРИЙ №", 65, 20, 140, delegate
			{
				RemoteCommand c = new RemoteCommand(); c.Cmd = RemoteCmd.ScriptPlay; c.PlayerNumber = (int)nudScript.Value; SendRaw(c);
			});
			AddBtn(g6, "СТОП СЦЕНАРИЙ", 210, 20, 110, delegate { Send(RemoteCmd.ScriptStop); });
			AddBtn(g6, "СБРОС ВРЕМЕНИ (ResetGameTime)", 330, 20, 220, delegate { Send(RemoteCmd.TimeResetOnly); });
			AddBtn(g6, "ТАЙМАУТ общий (без точки)", 560, 20, 170, delegate { Send(RemoteCmd.TimeoutRequest); });
			GroupBox g7 = new GroupBox(); g7.Text = "Доп. вывод: состояние потока"; g7.Left = 10; g7.Top = 380; g7.Width = 740; g7.Height = 72;
			Controls.Add(g7);
			extValue = new Label(); extValue.Text = "--:--"; extValue.Font = new Font("Consolas", 24f, FontStyle.Bold); extValue.AutoSize = true; extValue.Left = 15; extValue.Top = 18;
			g7.Controls.Add(extValue);
			extState = new Label(); extState.Text = "РЕЖИМ: - | СИГНАЛ: нет"; extState.AutoSize = true; extState.Left = 170; extState.Top = 20;
			g7.Controls.Add(extState);
			extFrame = new Label(); extFrame.Text = ""; extFrame.AutoSize = true; extFrame.Left = 170; extFrame.Top = 44;
			g7.Controls.Add(extFrame);
			logBox = new TextBox();
			logBox.Multiline = true; logBox.ReadOnly = true;
			logBox.ScrollBars = ScrollBars.Vertical;
			logBox.Left = 10; logBox.Top = 460; logBox.Width = 740; logBox.Height = 270;
			logBox.Font = UiFonts.Mono(9f, FontStyle.Regular);
			logBox.BackColor = Color.Black; logBox.ForeColor = Color.Lime;
			Controls.Add(logBox);
			AppendLog("Эмулятор готов: команды повторяют пакеты физического пульта 1 в 1.");
		}

		private void AppendLog(string s)
		{
			if (IsDisposed) return;
			if (InvokeRequired) { BeginInvoke((Action)delegate { AppendLog(s); }); return; }
			logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine);
		}
	}	public static class DevGuard
	{
		// SHA1 супер-пароля ********** (UTF-8). Сам пароль в коде отсутствует.
		public const string SuperHash = "69f29559e1fdf3fb2301d62a07fb0dd1cee5dc3b";

		// БЛОК 5: минимальная длина пароля снижена с 6 до 5 символов по решению
		// заказчика (прежнее ограничение отвергало, например, пароль «admin»).
		// Константа используется во всех точках задания/смены паролей.
		public const int MinPasswordLength = 5;

		public static bool IsSuper(string pass)
		{
			// БЛОК 4, F7-03: сравнение хэшей в постоянном времени.
			return pass != null && WebServer.FixedTimeStrEquals(WebServer.Sha1(pass), SuperHash);
		}

		public static string Sign(string adminHash, string enable)
		{
			return WebServer.Sha1((adminHash == null ? "" : adminHash) + "|" + enable);
		}

		public static bool WebEnabled(Tablo.TConfig cfg)
		{
			if (cfg == null || cfg.WebEnable != "1")
			{
				return false;
			}
			// БЛОК 4, F7-01: веб-сервер больше не может работать «без пароля»:
			// если пароль разработчика не задан (DevPassHash пуст), сервер не
			// поднимается даже при совпадающей подписи включения.
			if (string.IsNullOrEmpty(cfg.DevPassHash))
			{
				return false;
			}
			return cfg.WebEnableToken == Sign(cfg.DevPassHash, "1");
		}

		// БЛОК 4, F7-03: проверка пароля разработчика поддерживает два формата хэша —
		// новый PBKDF2 («pbkdf2$итерации$соль$хэш») и старый SHA-1. При успешном входе
		// со старым хэшем он ПРОЗРАЧНО заменяется на PBKDF2 (подпись включения
		// перевыпускается, настройки сохраняются) — менять пароль вручную не нужно.
		// БЛОК 5: диагностика для журнала: в каком состоянии хранится хэш пароля входа.
		// Помогает понять причину «неверный пароль» без раскрытия самого хэша.
		public static string DescribeHashState(string hash)
		{
			if (string.IsNullOrEmpty(hash))
			{
				return "не задан";
			}
			if (hash.StartsWith("pbkdf2$"))
			{
				return "PBKDF2";
			}
			if (hash.Length == 40)
			{
				return "SHA-1 (старый формат)";
			}
			return "НЕИЗВЕСТНЫЙ ФОРМАТ (длиной " + hash.Length.ToString() + ")";
		}

		public static bool VerifyDev(Tablo.TConfig cfg, string pass)
		{
			if (cfg == null || pass == null)
			{
				return false;
			}
			if (IsSuper(pass))
			{
				return true;
			}
			bool legacy;
			bool ok = WebServer.VerifyPassword(pass, cfg.DevPassHash, out legacy);
			if (ok && legacy)
			{
				try
				{
					cfg.DevPassHash = WebServer.HashPassword(pass);
					cfg.WebEnableToken = Sign(cfg.DevPassHash, cfg.WebEnable);
					cfg.Save();
					SerialPortLog.Write("БЕЗОПАСНОСТЬ: пароль входа разработчика переведён с SHA-1 на PBKDF2 (подпись включения перевыпущена)");
				}
				catch (Exception ex)
				{
					Diag.Note("VerifyDev upgrade", ex);
				}
			}
			return ok;
		}
	}

	public static class DevUi
	{
		// БЛОК 5: служебный ввод пароля с флажком «Запомнить пароль».
		// showRemember=true только в диалоге ВХОДА в режим разработчика; в служебных
		// запросах (смена/сброс пароля) флажок не показывается.
		private static string PromptPasswordCore(string title, bool showRemember, out bool remember)
		{
			remember = false;
			Form f = new Form();
			f.Text = title;
			f.Size = new Size(320, showRemember ? 185 : 150);
			f.StartPosition = FormStartPosition.CenterScreen;
			f.FormBorderStyle = FormBorderStyle.FixedDialog;
			f.MaximizeBox = false;
			f.MinimizeBox = false;
			TextBox t = new TextBox();
			t.PasswordChar = '*';
			t.Location = new Point(20, 20);
			t.Size = new Size(260, 20);
			CheckBox cr = new CheckBox();
			cr.Text = "Запомнить пароль (не спрашивать до блокировки)";
			cr.Location = new Point(20, 48);
			cr.Size = new Size(260, 20);
			cr.Visible = showRemember;
			Button ok = new Button();
			ok.Text = "ОК";
			ok.Location = new Point(20, showRemember ? 80 : 55);
			ok.Size = new Size(120, 23);
			ok.DialogResult = DialogResult.OK;
			Button cc = new Button();
			cc.Text = "Отмена";
			cc.Location = new Point(160, showRemember ? 80 : 55);
			cc.Size = new Size(120, 23);
			cc.DialogResult = DialogResult.Cancel;
			f.Controls.Add(t);
			f.Controls.Add(cr);
			f.Controls.Add(ok);
			f.Controls.Add(cc);
			f.AcceptButton = ok;
			f.CancelButton = cc;
			if (f.ShowDialog() == DialogResult.OK)
			{
				remember = cr.Checked;
				return t.Text;
			}
			return null;
		}

		// ПУНКТ 3: ввод ПРОИЗВОЛЬНОГО значения (не пароль — символы видны). Используется
		// для подтверждения опасных действий повторным вводом показанного значения.
		// Возвращает null при отмене.
		public static string PromptText(string title, string prompt)
		{
			Form f2 = new Form();
			f2.Text = title;
			f2.Size = new Size(420, 240);
			f2.StartPosition = FormStartPosition.CenterScreen;
			f2.FormBorderStyle = FormBorderStyle.FixedDialog;
			f2.MaximizeBox = false;
			f2.MinimizeBox = false;
			// ПУНКТ 1 (контраст): цвета задаём явно — текст читается при любой теме Windows.
			f2.BackColor = SystemColors.Control;
			f2.ForeColor = SystemColors.ControlText;
			Label lp = new Label();
			lp.Text = prompt;
			lp.Location = new Point(16, 12);
			lp.Size = new Size(376, 110);
			lp.ForeColor = SystemColors.ControlText;
			TextBox t = new TextBox();
			t.Location = new Point(16, 128);
			t.Size = new Size(376, 22);
			t.BackColor = Color.White;
			t.ForeColor = Color.Black;
			Button ok = new Button();
			ok.Text = "ОК";
			ok.Location = new Point(16, 160);
			ok.Size = new Size(180, 26);
			ok.DialogResult = DialogResult.OK;
			Button cc = new Button();
			cc.Text = "Отмена";
			cc.Location = new Point(212, 160);
			cc.Size = new Size(180, 26);
			cc.DialogResult = DialogResult.Cancel;
			f2.Controls.Add(lp);
			f2.Controls.Add(t);
			f2.Controls.Add(ok);
			f2.Controls.Add(cc);
			f2.AcceptButton = ok;
			f2.CancelButton = cc;
			f2.Shown += delegate { t.Focus(); };
			if (f2.ShowDialog() == DialogResult.OK)
			{
				return t.Text;
			}
			return null;
		}
		
		public static string PromptPassword(string title)
		{
			bool dummy;
			return PromptPasswordCore(title, false, out dummy);
		}

		// БЛОК 5: диалог входа в режим разработчика с флажком «Запомнить пароль».
		public static string PromptPasswordRemember(string title, out bool remember)
		{
			return PromptPasswordCore(title, true, out remember);
		}

		// БЛОК 5: задание пароля, когда он НЕ задан. Один диалог вместо цепочки
		// запросов; супер-пароль не требуется. При несовпадении/коротком пароле
		// диалог показывается снова (введённый текст сохраняется для исправления).
		public static string PromptCreatePassword(string header, out bool remember)
		{
			remember = false;
			Form f = new Form();
			f.Text = "Задание пароля режима разработчика";
			f.Size = new Size(360, 250);
			f.StartPosition = FormStartPosition.CenterScreen;
			f.FormBorderStyle = FormBorderStyle.FixedDialog;
			f.MaximizeBox = false;
			f.MinimizeBox = false;
			Label lh = new Label();
			lh.Text = header;
			lh.Location = new Point(20, 10);
			lh.Size = new Size(310, 40);
			Label l1 = new Label();
			l1.Text = "Пароль:";
			l1.Location = new Point(20, 55);
			l1.Size = new Size(120, 20);
			TextBox t1 = new TextBox();
			t1.PasswordChar = '*';
			t1.Location = new Point(20, 78);
			t1.Size = new Size(300, 20);
			Label l2 = new Label();
			l2.Text = "Повторите:";
			l2.Location = new Point(20, 106);
			l2.Size = new Size(120, 20);
			TextBox t2 = new TextBox();
			t2.PasswordChar = '*';
			t2.Location = new Point(20, 129);
			t2.Size = new Size(300, 20);
			CheckBox cr = new CheckBox();
			cr.Text = "Запомнить пароль (не спрашивать до блокировки)";
			cr.Location = new Point(20, 158);
			cr.Size = new Size(300, 20);
			Button ok = new Button();
			ok.Text = "Задать";
			ok.Location = new Point(20, 186);
			ok.Size = new Size(140, 23);
			ok.DialogResult = DialogResult.OK;
			Button cc = new Button();
			cc.Text = "Отмена";
			cc.Location = new Point(180, 186);
			cc.Size = new Size(140, 23);
			cc.DialogResult = DialogResult.Cancel;
			f.Controls.Add(lh);
			f.Controls.Add(l1);
			f.Controls.Add(t1);
			f.Controls.Add(l2);
			f.Controls.Add(t2);
			f.Controls.Add(cr);
			f.Controls.Add(ok);
			f.Controls.Add(cc);
			f.AcceptButton = ok;
			f.CancelButton = cc;
			while (true)
			{
				if (f.ShowDialog() != DialogResult.OK)
				{
					return null;
				}
				// БЛОК 5: минимум DevGuard.MinPasswordLength (5) символов.
				if (t1.Text.Length >= DevGuard.MinPasswordLength && t1.Text == t2.Text)
				{
					remember = cr.Checked;
					return t1.Text;
				}
				MessageBox.Show("Пароли не совпали, пусты или короче " + DevGuard.MinPasswordLength.ToString() + " символов.", "Режим разработчика", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}

	}

	public static class DevSession
	{
		public static bool Remembered = false;

		// БЛОК 5: разбор «минуты блокировки по бездействию» для диалога входа
		// (раньше разбор жил только внутри DevAdminForm). Ошибка -> 15 минут.
		public static int ParseIdleMinutes(string text)
		{
			int v;
			if (int.TryParse((text == null ? "" : text).Trim(), out v) && v > 0 && v < 1440)
			{
				return v;
			}
			return 15;
		}
		public static Form DevForm = null;
		private static System.Windows.Forms.Timer _mon;
		private static int _idleLimitMin = 15;

		private struct LastInputInfo
		{
			public uint cbSize;
			public uint dwTime;
		}

		[System.Runtime.InteropServices.DllImport("user32.dll")]
		private static extern bool GetLastInputInfo(ref LastInputInfo plii);

		public static int IdleMs()
		{
			LastInputInfo li = new LastInputInfo();
			li.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(LastInputInfo));
			try
			{
				if (GetLastInputInfo(ref li))
				{
					int idle = unchecked(Environment.TickCount - (int)li.dwTime);
					return (idle < 0) ? 0 : idle;
				}
			}
			catch
			{
			}
			return 0;
		}

		public static void Grant(int idleMinutes)
		{
			Remembered = true;
			_idleLimitMin = (idleMinutes < 1) ? 1 : idleMinutes;
			if (_mon == null)
			{
				_mon = new System.Windows.Forms.Timer();
				_mon.Interval = 1000;
				_mon.Tick += MonitorTick;
			}
			_mon.Start();
		}

		public static void Forget()
		{
			Remembered = false;
			if (_mon != null)
			{
				_mon.Stop();
			}
		}

		public static void Revoke(string reason)
		{
			Remembered = false;
			if (_mon != null)
			{
				_mon.Stop();
			}
			if (DevForm != null && !DevForm.IsDisposed)
			{
				try
				{
					DevForm.BeginInvoke(new Action(delegate
					{
						try
						{
							DevForm.Close();
						}
						catch
						{
						}
					}));
				}
				catch
				{
					try
					{
						DevForm.Close();
					}
					catch
					{
					}
				}
			}
			SerialPortLog.Write("РЕЖИМ РАЗРАБОТЧИКА: сессия запоминания закрыта (" + reason + ")");
		}

		private static void MonitorTick(object sender, EventArgs e)
		{
			if (!Remembered)
			{
				_mon.Stop();
				return;
			}
			if (IdleMs() > _idleLimitMin * 60 * 1000)
			{
				Revoke("бездействие " + _idleLimitMin + " мин");
			}
		}
	}

	public class DevAdminForm : Form
	{
		private readonly Tablo.TConfig cfg;
		private CheckBox cbWeb;
		private CheckBox cbLan;
        private CheckBox cbHttps;
		private TextBox tbWebPort;
		private TextBox tbWebIp;
		private CheckBox cbLog;
		private CheckBox cbVmix;
		private CheckBox cbUdp;
		private CheckBox cbFile;
		private TextBox tbVmixHost;
		private TextBox tbVmixInput;
		private TextBox tbUdpHost;
		private TextBox tbFrame;
		private TextBox tbWebPass;
		private CheckBox cbRemember;
		private TextBox tbIdle;

		public DevAdminForm(Tablo.TConfig cfg, Size ownerSize)
		{
			this.cfg = cfg;
			this.Text = "Режим разработчика";
			this.StartPosition = FormStartPosition.CenterScreen;
			this.FormBorderStyle = FormBorderStyle.FixedDialog;
			this.MaximizeBox = false;
			this.Size = new Size(720, ownerSize.Height);
			Panel bottom = new Panel();
			bottom.Dock = DockStyle.Bottom;
			bottom.Height = 48;
			Button bApply = new Button();
			bApply.Text = "Применить";
			bApply.Size = new Size(120, 26);
			bApply.Location = new Point(10, 10);
			bApply.Click += delegate
			{
				this.SaveAll();
				this.DialogResult = DialogResult.OK;
				this.Close();
			};
			Button bInstant = new Button();
			bInstant.Text = "Момент. применение";
			bInstant.Size = new Size(150, 26);
			bInstant.Location = new Point(140, 10);
			bInstant.Click += delegate
			{
				this.SaveAll();
			};
			Button bCancel = new Button();
			bCancel.Text = "Отменить";
			bCancel.Size = new Size(120, 26);
			bCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
			bCancel.Click += delegate
			{
				this.DialogResult = DialogResult.Cancel;
				this.Close();
			};
			bottom.Controls.Add(bApply);
			bottom.Controls.Add(bInstant);
			bottom.Controls.Add(bCancel);
			bCancel.Location = new Point(bottom.ClientSize.Width - 130, 10);
			Panel scroll = new Panel();
			scroll.Dock = DockStyle.Fill;
			scroll.AutoScroll = true;
			int colW = (this.ClientSize.Width - 30) / 2;
			if (colW < 240)
			{
				colW = 240;
			}
			int xR = 10 + colW + 10;
			GroupBox g1 = new GroupBox();
			g1.Text = "Веб-сервер";
			g1.Location = new Point(10, 8);
			g1.Size = new Size(colW, 118);
			cbWeb = new CheckBox();
			cbWeb.Text = "Сервер включен (вывод и оператор)";
			cbWeb.Location = new Point(8, 18);
			cbWeb.Size = new Size(colW - 16, 20);
			cbWeb.Checked = (cfg.WebEnable == "1");
			Label lbPort = new Label();
			lbPort.Text = "Порт:";
			lbPort.Location = new Point(8, 45);
			lbPort.Size = new Size(40, 20);
			tbWebPort = new TextBox();
			tbWebPort.Location = new Point(50, 42);
			tbWebPort.Size = new Size(60, 20);
			tbWebPort.Text = cfg.WebPort;
			Label lbIp = new Label();
			lbIp.Text = "IP:";
			lbIp.Location = new Point(120, 45);
			lbIp.Size = new Size(28, 20);
			tbWebIp = new TextBox();
			tbWebIp.Location = new Point(150, 42);
			tbWebIp.Size = new Size(120, 20);
			tbWebIp.Text = string.IsNullOrEmpty(cfg.WebIp) ? WebServer.DetectLocalIp() : cfg.WebIp;
            			System.Collections.Generic.List<string> ipsNow = WebServer.LocalIpList();
			if (ipsNow.Count > 0 && tbWebIp.Text.Length > 0 && ipsNow.IndexOf(tbWebIp.Text) < 0)
			{
				if (MessageBox.Show("IP-адрес в поле (" + tbWebIp.Text + ") не совпадает ни с одним текущим адресом машины: " + string.Join(", ", ipsNow.ToArray()) + ".\nПодключение по указанному адресу невозможно. Подставить первый актуальный адрес (" + ipsNow[0] + ")?", "Режим разработчика", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
				{
					tbWebIp.Text = ipsNow[0];
				}
			}
			cbLan = new CheckBox();
			cbLan.Text = "Доступ из локальной сети";
			cbLan.Location = new Point(8, 68);
			cbLan.Size = new Size(colW - 16, 20);
			cbLan.Checked = (cfg.WebBindAll == "1");
			g1.Controls.Add(cbWeb);
			g1.Controls.Add(lbPort);
			g1.Controls.Add(tbWebPort);
			g1.Controls.Add(lbIp);
			g1.Controls.Add(tbWebIp);
			g1.Controls.Add(cbLan);
             			cbHttps = new CheckBox();
 			cbHttps.Text = "HTTPS (TLS-шифрование)";
 			cbHttps.Location = new Point(8, 92);
 			cbHttps.Size = new Size(colW - 16, 20);
 			cbHttps.Checked = (cfg.WebUseHttps == "1");
 			g1.Controls.Add(cbHttps);
			scroll.Controls.Add(g1);
			GroupBox g2 = new GroupBox();
			g2.Text = "Отдача времени (три способа)";
			g2.Location = new Point(10, 132);
			g2.Size = new Size(colW, 110);
			cbVmix = new CheckBox();
			cbVmix.Text = "В vMix (HTTP API)";
			cbVmix.Location = new Point(8, 16);
			cbVmix.Size = new Size(150, 20);
			tbVmixHost = new TextBox();
			tbVmixHost.Location = new Point(8, 38);
			tbVmixHost.Size = new Size(110, 20);
			tbVmixInput = new TextBox();
			tbVmixInput.Location = new Point(122, 38);
			tbVmixInput.Size = new Size(colW - 130, 20);
			cbUdp = new CheckBox();
			cbUdp.Text = "По UDP (JSON)";
			cbUdp.Location = new Point(8, 62);
			cbUdp.Size = new Size(110, 20);
			tbUdpHost = new TextBox();
			tbUdpHost.Location = new Point(122, 62);
			tbUdpHost.Size = new Size(colW - 130, 20);
			cbFile = new CheckBox();
			cbFile.Text = "Писать время в tablo_time.txt";
			cbFile.Location = new Point(8, 86);
			cbFile.Size = new Size(colW - 16, 20);
			g2.Controls.Add(cbVmix);
			g2.Controls.Add(tbVmixHost);
			g2.Controls.Add(tbVmixInput);
			g2.Controls.Add(cbUdp);
			g2.Controls.Add(tbUdpHost);
			g2.Controls.Add(cbFile);
			scroll.Controls.Add(g2);
			GroupBox g3 = new GroupBox();
			g3.Text = "Логи и кадр вывода";
			g3.Location = new Point(10, 246);
			g3.Size = new Size(colW, 100);
			cbLog = new CheckBox();
			cbLog.Text = "Вести лог пульта remote_com_log.txt";
			cbLog.Location = new Point(8, 16);
			cbLog.Size = new Size(colW - 16, 20);
			cbLog.Checked = cfg.RemoteLogEnable;
			Label lf = new Label();
			lf.Text = "Кадр:";
			lf.Location = new Point(8, 44);
			lf.Size = new Size(40, 20);
			tbFrame = new TextBox();
			tbFrame.Location = new Point(50, 41);
			tbFrame.Size = new Size(colW - 58, 20);
			tbFrame.Text = cfg.ExtFrameTemplate;
					Button bClearLogs = new Button();
		bClearLogs.Text = "Очистить логи";
		bClearLogs.Size = new Size(colW - 16, 23);
		bClearLogs.Location = new Point(8, 68);
		bClearLogs.Click += delegate
		{
			if (MessageBox.Show("Удалить содержимое ВСЕХ файлов-логов (remote_com_log.txt, crash_log.txt)? Действие необратимо, текущие логи будут потеряны.", "Режим разработчика", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
			{
				SerialPortLog.ClearAll();
				MessageBox.Show("Логи очищены. Ведение лога продолжается с новой строки.", "Режим разработчика", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
		};
		g3.Controls.Add(cbLog);
		g3.Controls.Add(lf);
		g3.Controls.Add(tbFrame);
		g3.Controls.Add(bClearLogs);
		scroll.Controls.Add(g3);
			GroupBox g4 = new GroupBox();
			g4.Text = "Пароль оператора веб-панели";
			g4.Location = new Point(xR, 8);
			g4.Size = new Size(colW, 56);
			tbWebPass = new TextBox();
			tbWebPass.PasswordChar = '*';
			tbWebPass.Location = new Point(8, 20);
			tbWebPass.Size = new Size(110, 20);
			Button bWeb = new Button();
			bWeb.Text = "Задать пароль";
			bWeb.Location = new Point(122, 18);
			bWeb.Size = new Size(colW - 130, 23);
			bWeb.Click += delegate
			{
				// БЛОК 4, F7-03: хэш PBKDF2 с солью (старый SHA-1 больше не записывается;
				// проверка входа понимает оба формата). БЛОК 5: минимум 5 символов.
				if (tbWebPass.Text.Length < DevGuard.MinPasswordLength)
				{
					MessageBox.Show("Введите новый пароль веб-оператора (не короче " + DevGuard.MinPasswordLength.ToString() + " символов).");
					return;
				}
				cfg.WebPassHash = WebServer.HashPassword(tbWebPass.Text);
				cfg.Save();
				WebServer.PassHash = cfg.WebPassHash;
				tbWebPass.Text = "";
				MessageBox.Show("Пароль веб-оператора обновлён.");
			};
			g4.Controls.Add(tbWebPass);
			g4.Controls.Add(bWeb);
			scroll.Controls.Add(g4);
			GroupBox g5 = new GroupBox();
			g5.Text = "Пароли входа и блокировка по бездействию";
			g5.Location = new Point(xR, 68);
			g5.Size = new Size(colW, 210);   // ПУНКТ 3: +34 px под кнопку сброса счётчика попыток
			Label lbIdle = new Label();
			lbIdle.Text = "Блокировка при бездействии, минут:";
			lbIdle.Location = new Point(8, 18);
			lbIdle.Size = new Size(colW - 60, 20);
			tbIdle = new TextBox();
			tbIdle.Location = new Point(colW - 48, 17);
			tbIdle.Size = new Size(40, 20);
			tbIdle.Text = cfg.DevIdleMinutes;
			cbRemember = new CheckBox();
			cbRemember.Location = new Point(8, 44);
			cbRemember.Size = new Size(colW - 16, 20);
			cbRemember.Text = "Запомнить пароль на " + cfg.DevIdleMinutes + " минут";
			Button bCh = new Button();
			bCh.Text = "Сменить пароль входа";
			bCh.Location = new Point(8, 74);
			bCh.Size = new Size(colW - 16, 23);
			bCh.Click += delegate
			{
				string cur = DevUi.PromptPassword("Текущий пароль входа");
				if (cur == null)
				{
					return;
				}
				// БЛОК 4, F7-03: проверка текущего пароля через VerifyDev (PBKDF2/SHA-1
				// + прозрачный апгрейд хэша при входе).
				if (!DevGuard.VerifyDev(cfg, cur))
				{
					MessageBox.Show("Неверный текущий пароль.");
					return;
				}
				string n1 = DevUi.PromptPassword("Новый пароль входа (не короче " + DevGuard.MinPasswordLength.ToString() + " символов)");
				if (n1 == null || n1.Length == 0)
				{
					return;
				}
				if (n1.Length < DevGuard.MinPasswordLength)   // БЛОК 5: было 6 (F7-06 блока 4)
				{
					MessageBox.Show("Новый пароль короче " + DevGuard.MinPasswordLength.ToString() + " символов — не принят.");
					return;
				}
				string n2 = DevUi.PromptPassword("Повторите новый пароль");
				if (n1 != n2)
				{
					MessageBox.Show("Повтор не совпал.");
					return;
				}
				cfg.DevPassHash = WebServer.HashPassword(n1);   // БЛОК 4, F7-03: PBKDF2
				cfg.WebEnableToken = DevGuard.Sign(cfg.DevPassHash, cfg.WebEnable);
				cfg.Save();
				MessageBox.Show("Пароль входа изменён.");
			};
			Button bRs = new Button();
			bRs.Text = "Сброс пароля входа (супер)";
			bRs.Location = new Point(8, 106);
			bRs.Size = new Size(colW - 16, 23);
			bRs.Click += delegate
			{
				string s = DevUi.PromptPassword("Супер-пароль для сброса");
				if (s == null)
				{
					return;
				}
				if (!DevGuard.IsSuper(s))
				{
					MessageBox.Show("Неверный супер-пароль.");
					return;
				}
				cfg.DevPassHash = "";
				cfg.WebEnable = "0";
				cfg.WebEnableToken = "";
				cfg.Save();
				WebServer.Stop();
				DevSession.Forget();
				MessageBox.Show("Пароль входа сброшен, сервер остановлен.");
			};
			Button bRsW = new Button();
			bRsW.Text = "Сброс пароля веб-оператора (супер)";
			bRsW.Location = new Point(8, 138);
			bRsW.Size = new Size(colW - 16, 23);
			bRsW.Click += delegate
			{
				string s = DevUi.PromptPassword("Супер-пароль для сброса");
				if (s == null)
				{
					return;
				}
				if (!DevGuard.IsSuper(s))
				{
					MessageBox.Show("Неверный супер-пароль.");
					return;
				}
				cfg.WebPassHash = "";
				cfg.Save();
				WebServer.PassHash = "";
				MessageBox.Show("Пароль веб-оператора сброшен.");
			};
			g5.Controls.Add(lbIdle);
			g5.Controls.Add(tbIdle);
			g5.Controls.Add(cbRemember);
			g5.Controls.Add(bCh);
			g5.Controls.Add(bRs);
			// ================== ПУНКТ 3: РУЧНОЙ СБРОС СЧЁТЧИКА НЕУДАЧНЫХ ПОПЫТОК ВХОДА ==================
			// Сбрасывает уровень нарастающей паузы по ВСЕМ устройствам (веб-вход).
			// Подтверждается повторным вводом показанного значения — защита от случайного нажатия.
			Button bRsF = new Button();
			bRsF.Text = "Сброс счётчика попыток входа";
			bRsF.Location = new Point(8, 170);
			bRsF.Size = new Size(colW - 16, 23);
			bRsF.Click += delegate
			{
				int devCnt = 0;
				int maxWait = 0;
				int lvl = WebServer.GetFailStateAll(out devCnt, out maxWait);
				if (devCnt <= 0 && lvl <= 0)
				{
					MessageBox.Show("Счётчик неудачных попыток входа пуст - сбрасывать нечего.");
					return;
				}
				string want = lvl.ToString();
				string msg = "Устройств в счётчике: " + devCnt + "\r\n"
					+ "Текущий уровень паузы: " + want + "\r\n"
					+ "Остаток самой длинной паузы: " + WebServer.FmtPause(maxWait) + "\r\n\r\n"
					+ "Для подтверждения сброса введите это значение ещё раз:  " + want;
				string got = DevUi.PromptText("Сброс счётчика попыток входа", msg);
				if (got == null)
				{
					return;
				}
				if (got.Trim() != want)
				{
					MessageBox.Show("Введённое значение не совпало - сброс НЕ выполнен.");
					return;
				}
				int n = WebServer.ResetLoginFails();
				MessageBox.Show("Счётчик неудачных попыток входа сброшен.\r\nУстройств сброшено: " + n + ".\r\nУровень паузы снова 0 (следующая неудача - 30 сек).");
			};
			g5.Controls.Add(bRsW);
			g5.Controls.Add(bRsF);
			scroll.Controls.Add(g5);
			this.Controls.Add(scroll);
			this.Controls.Add(bottom);
			DevSession.DevForm = this;
			cbRemember.Checked = DevSession.Remembered;
			cbRemember.CheckedChanged += delegate
			{
				if (cbRemember.Checked)
				{
					DevSession.Grant(ParseIdle());
				}
				else
				{
					DevSession.Forget();
				}
			};
			tbIdle.TextChanged += delegate
			{
				cbRemember.Text = "Запомнить пароль на " + ParseIdle().ToString() + " минут";
			};
			string vmApi = "0";
			string vmHost = "192.168.1.100:8088";
			string vmInput = "Title.xaml";
			string udApi = "0";
			string udHost = "192.168.1.100:9999";
			string fl = "0";
			// БЛОК 4, F4-04/F8-07: чтение через TextIO (BOM/UTF-8/ANSI), а сбой
			// чтения больше не проглатывается молча — уходит в диагностику.
			try
			{
				string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tablo_stream.txt");
				if (File.Exists(p))
				{
					foreach (string ln in TextIO.ReadAllLines(p))
					{
						int i = ln.IndexOf('=');
						if (i < 0)
						{
							continue;
						}
						string k = ln.Substring(0, i).Trim();
						string v = ln.Substring(i + 1).Trim();
						if (k == "VmixApi")
						{
							vmApi = v;
						}
						else if (k == "VmixHost")
						{
							vmHost = v;
						}
						else if (k == "VmixInput")
						{
							vmInput = v;
						}
						else if (k == "Udp")
						{
							udApi = v;
						}
						else if (k == "UdpHost")
						{
							udHost = v;
						}
						else if (k == "File")
						{
							fl = v;
						}
					}
				}
			}
			catch (Exception exStream)
			{
				Diag.Note("Чтение tablo_stream.txt (DevAdminForm)", exStream);
			}
			cbVmix.Checked = (vmApi == "1");
			tbVmixHost.Text = vmHost;
			tbVmixInput.Text = vmInput;
			cbUdp.Checked = (udApi == "1");
			tbUdpHost.Text = udHost;
			cbFile.Checked = (fl == "1");
		}

		private int ParseIdle()
		{
			int v = 15;
			try
			{
				v = int.Parse(tbIdle.Text.Trim());
			}
			catch
			{
			}
			if (v < 1)
			{
				v = 1;
			}
			if (v > 240)
			{
				v = 240;
			}
			return v;
		}

		private int WebPortNum()
		{
			int p = 8642;
			try
			{
				p = int.Parse(cfg.WebPort);
			}
			catch
			{
			}
			if (p < 1024 || p > 65535)
			{
				p = 8642;
			}
			return p;
		}

		private void SaveAll()
		{
			// БЛОК 6, З10: запоминаем ПРЕЖНИЕ значения параметров веб-сервера.
			// Сервер будет перезапущен, только если хотя бы один из них реально изменился.
			string oldWebEnable = cfg.WebEnable;
			string oldWebPort = cfg.WebPort;
			string oldWebBindAll = cfg.WebBindAll;
			string oldWebUseHttps = cfg.WebUseHttps;
			cfg.WebEnable = cbWeb.Checked ? "1" : "0";
			cfg.WebEnableToken = DevGuard.Sign(cfg.DevPassHash, cfg.WebEnable);
			cfg.RemoteLogEnable = cbLog.Checked;
			SerialPortLog.Enabled = cbLog.Checked;
			cfg.ExtFrameTemplate = tbFrame.Text.Trim();
			cfg.DevIdleMinutes = ParseIdle().ToString();
			cfg.WebPort = tbWebPort.Text.Trim();
			cfg.WebBindAll = cbLan.Checked ? "1" : "0";
            cfg.WebUseHttps = cbHttps.Checked ? "1" : "0";
			cfg.WebIp = tbWebIp.Text.Trim();
            			System.Collections.Generic.List<string> ipsSave = WebServer.LocalIpList();
			if (ipsSave.Count > 0 && cfg.WebIp.Length > 0 && ipsSave.IndexOf(cfg.WebIp) < 0)
			{
				MessageBox.Show("Внимание: указанный IP (" + cfg.WebIp + ") не является текущим адресом машины (" + string.Join(", ", ipsSave.ToArray()) + "). Сервер слушает все адреса, поэтому подключаться нужно по одному из списка или по localhost; значение сохранено как есть.", "Режим разработчика", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
			cfg.Save();
			try
			{
				string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tablo_stream.txt");
				// БЛОК 4, F4-03/F8-09: атомарная запись с BOM (русские названия полей
				// в vMix читаются корректно), сбой больше не проглатывается молча.
				TextIO.WriteAll(p, "VmixApi=" + (cbVmix.Checked ? "1" : "0") + "\r\nVmixHost=" + tbVmixHost.Text + "\r\nVmixInput=" + tbVmixInput.Text + "\r\nVmixField=TimeText\r\nUdp=" + (cbUdp.Checked ? "1" : "0") + "\r\nUdpHost=" + tbUdpHost.Text + "\r\nFile=" + (cbFile.Checked ? "1" : "0") + "\r\n", true);
			}
			catch (Exception exStreamSave)
			{
				Diag.Note("Сохранение tablo_stream.txt", exStreamSave);
				MessageBox.Show("Не удалось сохранить tablo_stream.txt: " + exStreamSave.Message, "Режим разработчика", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
			if (cbRemember.Checked)
			{
				DevSession.Grant(ParseIdle());
			}
			// БЛОК 6, З10: перезапуск веб-сервера ТОЛЬКО при реальных изменениях его
			// параметров (включен/порт/доступ из сети/HTTPS). Остальные настройки окна
			// (логи, кадр вывода, vMix/UDP, пароль веб-оператора) применяются БЕЗ обрыва
			// подключений операторов — раньше сервер падал и поднимался при каждом «Применить».
			bool webChanged = (oldWebEnable != cfg.WebEnable) || (oldWebPort != cfg.WebPort) ||
			(oldWebBindAll != cfg.WebBindAll) || (oldWebUseHttps != cfg.WebUseHttps);
			bool wantWeb = DevGuard.WebEnabled(cfg);
			if (webChanged)
			{
				WebServer.Stop();
				if (wantWeb)
				{
					WebServer.Start(WebPortNum(), cfg.WebBindAll == "1", cfg.WebIp, cfg.WebUseHttps == "1");
				}
			}
			else if (wantWeb && !WebServer.IsRunning)
			{
				// параметры не менялись, но сервер почему-то не поднят (например, не стартовал
				// при запуске программы) — поднимаем без остановки.
				WebServer.Start(WebPortNum(), cfg.WebBindAll == "1", cfg.WebIp, cfg.WebUseHttps == "1");
			}
			else if (!wantWeb && WebServer.IsRunning)
			{
				WebServer.Stop();
			}
		}

		protected override void OnFormClosed(FormClosedEventArgs e)
		{
			if (DevSession.DevForm == this)
			{
				DevSession.DevForm = null;
			}
			base.OnFormClosed(e);
		}
	}

	 public class WebSession
 {
 	public string Token;
 	public string Finger;
 	public DateTime LastCmd;
 	public bool Super;
 	public string Ip;
 	public string Ua;
 	public DateTime Created;   // БЛОК 4, F7-07: абсолютный срок жизни сессии (8 ч)
 	public string Sid;         // БЛОК 4, F7-11: короткий публичный идентификатор (не токен)
 }

	public static class WebServer
	{
		private static System.Net.HttpListener _listener;
		private static System.Threading.Thread _thread;
		private static readonly object _sync = new object();
		public static string StateCache = "{}";
		public static string PubCache = "{}";   // БЛОК 3, Q15: публичный урезанный state для /display и /pub.json
		public static Func<string, int, int> CmdProvider;   // БЛОК 3, F3-09: 0=выполнено, 1=неизвестная команда, 2=отклонено защитой Q6, 3=сбой
		public static Func<string, string, bool> CmdTextProvider;
		public static string PassHash = "";
		public static int IdleLimitMin = 30;
		public static int MaxDevices = 2;
        public static bool GodMode = false;
		private static readonly System.Collections.Generic.Dictionary<string, WebSession> _tokens = new System.Collections.Generic.Dictionary<string, WebSession>();

		// БЛОК 4, F9-02: один общий экземпляр SHA1 под блокировкой вместо создания
		// нового объекта на каждый вызов (SHA1 не потокобезопасен — поэтому lock).
		private static readonly System.Security.Cryptography.SHA1 _sha1 = System.Security.Cryptography.SHA1.Create();
		private static readonly object _sha1Lock = new object();
		private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

		public static string Sha1(string s)
		{
			byte[] r;
			lock (_sha1Lock)
			{
				r = _sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s ?? ""));
			}
			System.Text.StringBuilder sb = new System.Text.StringBuilder();
			for (int i = 0; i < r.Length; i++)
			{
				sb.Append(r[i].ToString("x2"));
			}
			return sb.ToString();
		}

		// ================== БЛОК 4, F7-03: PBKDF2-хэширование паролей ==================
		// Формат сохранённого хэша: "pbkdf2$<итерации>$<соль base64>$<хэш base64>".
		// Старые SHA-1-хэши (40 hex-символов) принимаются для ВХОДА и прозрачно
		// заменяются на PBKDF2 (см. DevGuard.VerifyDev и /login). Сравнение —
		// в постоянном времени (нельзя подобрать по времени ответа).
		public const int Pbkdf2Iterations = 20000;

		public static string HashPassword(string pass)
		{
			byte[] salt = new byte[16];
			// .NET 3.5: RNGCryptoServiceProvider ещё НЕ реализует IDisposable — без using.
			System.Security.Cryptography.RNGCryptoServiceProvider rng = new System.Security.Cryptography.RNGCryptoServiceProvider();
			rng.GetBytes(salt);
			return Pbkdf2(pass, salt, Pbkdf2Iterations);
		}

		private static string Pbkdf2(string pass, byte[] salt, int iters)
		{
			System.Security.Cryptography.Rfc2898DeriveBytes k = new System.Security.Cryptography.Rfc2898DeriveBytes(System.Text.Encoding.UTF8.GetBytes(pass ?? ""), salt, iters);
			byte[] h = k.GetBytes(32);
			return "pbkdf2$" + iters.ToString(Inv) + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(h);
		}

		public static bool VerifyPassword(string pass, string stored, out bool legacySha1)
		{
			legacySha1 = false;
			if (string.IsNullOrEmpty(stored))
			{
				return false;
			}
			if (stored.StartsWith("pbkdf2$"))
			{
				string[] p = stored.Split(new char[] { '$' });
				if (p.Length != 4)
				{
					return false;
				}
				int it;
				byte[] salt;
				byte[] want;
				if (!int.TryParse(p[1], out it) || it < 1000 || it > 500000)
				{
					return false;
				}
				try
				{
					salt = Convert.FromBase64String(p[2]);
					want = Convert.FromBase64String(p[3]);
				}
				catch
				{
					return false;
				}
				if (want.Length == 0)
				{
					return false;
				}
				byte[] got;
				try
				{
					System.Security.Cryptography.Rfc2898DeriveBytes k = new System.Security.Cryptography.Rfc2898DeriveBytes(System.Text.Encoding.UTF8.GetBytes(pass ?? ""), salt, it);
					got = k.GetBytes(want.Length);
				}
				catch
				{
					return false;
				}
				return FixedTimeEquals(got, want);
			}
			// старый формат — «голый» SHA-1 (40 hex)
			legacySha1 = true;
			return FixedTimeStrEquals(Sha1(pass), stored);
		}

		public static bool FixedTimeEquals(byte[] a, byte[] b)
		{
			if (a == null || b == null || a.Length != b.Length)
			{
				return false;
			}
			int diff = 0;
			for (int i = 0; i < a.Length; i++)
			{
				diff |= (int)(a[i] ^ b[i]);
			}
			return diff == 0;
		}

		public static bool FixedTimeStrEquals(string a, string b)
		{
			if (a == null || b == null || a.Length != b.Length)
			{
				return false;
			}
			int diff = 0;
			for (int i = 0; i < a.Length; i++)
			{
				diff |= (int)(a[i] ^ b[i]);
			}
			return diff == 0;
		}

		// БЛОК 4, F3-11: вместо Regex.Unescape (он раскрывает ЛЮБЫЕ escape-последовательности)
		// — ограниченный декодер клиентского uenc(): только «обратная косая + |», двойная
		// обратная косая и юникод-эскейпы u+4hex (управляющие символы не пропускаются).
		// Разделитель «|» остаётся защищённым от подделки.
		public static string UnescapeText(string s)
		{
			if (s == null || s.IndexOf('\\') < 0)
			{
				return s;
			}
			System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
			for (int i = 0; i < s.Length; i++)
			{
				char c = s[i];
				if (c == '\\' && i + 1 < s.Length)
				{
					char n = s[i + 1];
					if (n == 'u' && i + 5 < s.Length)
					{
						int code;
						if (int.TryParse(s.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber, Inv, out code) && code >= 32 && code <= 0xFFFF)
						{
							sb.Append((char)code);
							i += 5;
							continue;
						}
					}
					if (n == '|' || n == '\\')
					{
						sb.Append(n);
						i++;
						continue;
					}
				}
				sb.Append(c);
			}
			return sb.ToString();
		}

		// БЛОК 4, F7-03: frmMain подписывается сюда, чтобы при прозрачном апгрейде
		// хэша веб-оператора (SHA-1 -> PBKDF2) новый хэш сохранялся в config.xml.
		public static Action<string> PassHashUpgraded;

		// БЛОК 4, F7-08: помним, что сервер поднят по HTTPS (для HSTS-заголовка).
		private static volatile bool _useHttps = false;

		// БЛОК 4, F7-07: заголовки безопасности для всех ответов.
		private static void AddSecurityHeaders(System.Net.HttpListenerContext ctx, bool isHtml)
		{
			try
			{
				ctx.Response.AddHeader("X-Content-Type-Options", "nosniff");
				ctx.Response.AddHeader("X-Frame-Options", "DENY");
				ctx.Response.AddHeader("Referrer-Policy", "no-referrer");
				if (isHtml)
				{
					ctx.Response.AddHeader("Content-Security-Policy", "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src data:");
				}
				if (_useHttps)
				{
					ctx.Response.AddHeader("Strict-Transport-Security", "max-age=31536000");
				}
			}
			catch
			{
			}
		}

			public static string DetectLocalIp()
	{
		System.Collections.Generic.List<string> l = LocalIpList();
		return (l.Count > 0) ? l[0] : "";
	}

	public static System.Collections.Generic.List<string> LocalIpList()
	{
		System.Collections.Generic.List<string> res = new System.Collections.Generic.List<string>();
		try
		{
			foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
			{
				if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
					ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
				{
					continue;
				}
				System.Net.NetworkInformation.IPInterfaceProperties pp = ni.GetIPProperties();
				bool gw = (pp != null && pp.GatewayAddresses != null && pp.GatewayAddresses.Count > 0);
				System.Collections.Generic.List<string> mine = new System.Collections.Generic.List<string>();
				foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ua in pp.UnicastAddresses)
				{
					if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
					{
						mine.Add(ua.Address.ToString());
					}
				}
				if (gw)
				{
					res.InsertRange(0, mine);
				}
				else
				{
					res.AddRange(mine);
				}
			}
		}
		catch
		{
		}
		return res;
	}

		private static string FingerOf(System.Net.HttpListenerContext ctx)
		{
			string ip = "-";
			try
			{
				ip = ctx.Request.RemoteEndPoint.Address.ToString();
			}
			catch
			{
			}
			string ua = ctx.Request.Headers["User-Agent"] ?? "";
			return Sha1(ip + "|" + ua);
		}

		private static void PurgeExpired()
		{
			lock (_sync)
			{
				System.Collections.Generic.List<string> dead = new System.Collections.Generic.List<string>();
				foreach (System.Collections.Generic.KeyValuePair<string, WebSession> kv in _tokens)
				{
					if ((DateTime.Now - kv.Value.LastCmd).TotalMinutes > IdleLimitMin)
					{
						dead.Add(kv.Key);
					}
				}
				foreach (string k in dead)
				{
					_tokens.Remove(k);
				}
			}
		}

				public static void Start(int port, bool bindAll, string ip, bool useHttps)
	{
		lock (_sync)
		{
			if (_thread != null)
			{
				return;
			}
		}
		string scheme = useHttps ? "https" : "http";
		_useHttps = useHttps;   // БЛОК 4, F7-08: для HSTS-заголовка
		System.Collections.Generic.List<string> candidates = new System.Collections.Generic.List<string>();
		if (bindAll)
		{
			candidates.Add(scheme + "://+:" + port + "/");
		}
		candidates.Add(scheme + "://localhost:" + port + "/");
		foreach (string px in candidates)
		{
			// БЛОК 4, F3-15: слушатель объявлен ВНЕ try — при неудаче его нужно закрыть,
			// иначе объект остаётся «полуподнятым» и удерживает порт до перезапуска.
			System.Net.HttpListener l = new System.Net.HttpListener();
			try
			{
				l.Prefixes.Add(px);
				l.Start();
				_listener = l;
				if (px.StartsWith(scheme + "://+"))
				{
					SerialPortLog.Write("WEB: сервер запущен на " + px + " (все адреса: localhost, " + (string.IsNullOrEmpty(ip) ? DetectLocalIp() : ip) + ", имя машины)");
				}
				else
				{
					SerialPortLog.Write("WEB: сервер запущен на " + px + " (только localhost)");
				}
				System.Threading.Thread t = new System.Threading.Thread(Loop);
				t.IsBackground = true;
				_thread = t;
				t.Start();
				return;
			}
			catch (Exception ex)
			{
				try { l.Close(); } catch { }
				SerialPortLog.Write("WEB: префикс " + px + " не поднялся: " + ex.Message);
			}
		}
		SerialPortLog.Write("WEB: НЕ УДАЛОСЬ поднять сервер. Для сети один раз от администратора: netsh http add urlacl url=" + scheme + "://+:" + port + "/ user=Everyone");
		if (useHttps)
		{
			SerialPortLog.Write("WEB: для HTTPS привяжите сертификат один раз от администратора: netsh http add sslcert ipport=0.0.0.0:" + port + " certhash=THUMBPRINT appid={00000000-0000-0000-0000-000000000000}");
			// БЛОК 4, F7-08: причина отказа HTTPS объясняется явно, с подсказкой для XP.
			if (Environment.OSVersion.Version.Major < 6)
			{
				SerialPortLog.Write("WEB: HTTPS не поднят. На Windows XP сертификат привязывается командой: httpcfg set ssl -i 0.0.0.0:" + port + " -h THUMBPRINT (httpcfg.exe входит в Windows Support Tools).");
			}
			else
			{
				SerialPortLog.Write("WEB: HTTPS не поднят. Частые причины: сертификат не привязан к порту (netsh http add sslcert ...) или нет прав на префикс (netsh http add urlacl ...). В НЕзашифрованном режиме сервер НЕ запущен — при необходимости переключитесь на HTTP в режиме разработчика.");
			}
		}
	}

		// БЛОК 6, З10: признак «сервер реально поднят» — нужен, чтобы не перезапускать
		// веб-сервер при сохранении настроек, которые его НЕ касаются.
		public static bool IsRunning
		{
			get
			{
				lock (_sync)
				{
					return _listener != null && _thread != null;
				}
			}
		}

		// БЛОК 6, З8: счётчики активных веб-сессий для статус-строки программы:
		// обычные подключения (их ограничивает MaxDevices) и служебные (супер-пароль).
		public static void GetSessionCounts(out int regular, out int superCount)
		{
			regular = 0;
			superCount = 0;
			lock (_sync)
			{
				foreach (System.Collections.Generic.KeyValuePair<string, WebSession> kv in _tokens)
				{
					if (kv.Value.Super)
					{
						superCount++;
					}
					else
					{
						regular++;
					}
				}
			}
		}

		public static void Stop()
		{
			System.Net.HttpListener l;
			lock (_sync)
			{
				l = _listener;
				_listener = null;
				_thread = null;
			}
			try
			{
				if (l != null)
				{
					l.Stop();
					l.Close();
				}
			}
			catch
			{
			}
		}

		private static bool Auth(System.Net.HttpListenerContext ctx, out WebSession ses)
		{
			ses = null;
			// БЛОК 4, F7-07: токен передаётся в заголовке X-Tablo-Token (он не попадает
			// в URL, историю браузера и логи прокси). Старый вариант ?token= работает
			// как запасной — открытые в браузере страницы прошлой версии не «отвалятся».
			string tok = ctx.Request.Headers["X-Tablo-Token"];
			if (string.IsNullOrEmpty(tok))
			{
				tok = ctx.Request.QueryString["token"];
			}
			if (string.IsNullOrEmpty(tok))
			{
				return false;
			}
			lock (_sync)
			{
				WebSession s;
				if (!_tokens.TryGetValue(tok, out s))
				{
					return false;
				}
				if ((DateTime.Now - s.LastCmd).TotalMinutes > IdleLimitMin)
				{
					_tokens.Remove(tok);
					return false;
				}
				if (s.Finger != FingerOf(ctx))
				{
					_tokens.Remove(tok);
					SerialPortLog.Write("WEB: сессия разорвана - не совпал отпечаток устройства (подмена имени или адреса)");
					return false;
				}
				// БЛОК 4, F7-07: абсолютный срок жизни сессии — 8 часов, даже если
				// панель активно используется. Дальше — повторный вход по паролю.
				if ((DateTime.Now - s.Created).TotalHours > 8.0)
				{
					_tokens.Remove(tok);
					SerialPortLog.Write("WEB: сессия закрыта - превышен лимит 8 часов, требуется повторный вход");
					return false;
				}
				ses = s;
				return true;
			}
		}

		private static void Touch(WebSession s)
		{
			lock (_sync)
			{
				s.LastCmd = DateTime.Now;
			}
		}
       	// ================== ПУНКТ 3: ПОПЫТКИ ВХОДА И НАРАСТАЮЩИЕ ПАУЗЫ ==================
	// БЫЛО: плоская эскалация «5 неудач -> 60 с, 10 -> 10 мин, 15 -> 2 ч, 20 -> 24 ч»,
	// причём успешный вход обнулял и счётчик, и уровень.
	// СТАЛО: пауза назначается с ПЕРВОЙ неудачной попытки и нарастает по уровням:
	//	1 -> 30 с, 2 -> 45 с, 3 -> 60 с, 4 -> 2 мин, 5 -> 5 мин, 6 -> 15 мин,
	//	7 и далее -> 30 мин (потолок).
	// _failSeries — счётчик неудач ТЕКУЩЕЙ серии: обнуляется после успешного входа.
	// _failLevel  — уровень следующей паузы: после успешного входа НЕ обнуляется,
	//               сбрасывается ТОЛЬКО явно (веб-панель с повышенными правами
	//               либо меню «Режим разработчика») — см. ResetLoginFails().
	// Вход с повышенными правами (супер-пароль) временной заглушкой НЕ ограничивается.
	private static readonly int[] FailPauseSec = new int[] { 30, 45, 60, 120, 300, 900, 1800 };
	private static readonly System.Collections.Generic.Dictionary<string, int> _failSeries = new System.Collections.Generic.Dictionary<string, int>();
	private static readonly System.Collections.Generic.Dictionary<string, int> _failLevel = new System.Collections.Generic.Dictionary<string, int>();
	private static readonly System.Collections.Generic.Dictionary<string, DateTime> _failUntil = new System.Collections.Generic.Dictionary<string, DateTime>();
	// время последней неудачи (для «уборки» устаревших серий) и отметка последней «уборки».
	private static readonly System.Collections.Generic.Dictionary<string, DateTime> _failLast = new System.Collections.Generic.Dictionary<string, DateTime>();
	private static DateTime _lastHouseKeep = DateTime.Now;

	// Пауза в секундах для уровня level (нумерация с 1). Уровень выше таблицы -> потолок.
	public static int FailPauseForLevel(int level)
	{
		if (level < 1)
		{
			return 0;
		}
		if (level > FailPauseSec.Length)
		{
			level = FailPauseSec.Length;
		}
		return FailPauseSec[level - 1];
	}

	// Человекочитаемое название паузы (для журнала и сообщений).
	public static string FmtPause(int sec)
	{
		if (sec <= 0) return "без паузы";
		if (sec >= 60 && sec % 60 == 0) return (sec / 60).ToString(Inv) + " мин";
		if (sec >= 60) return ((double)sec / 60.0).ToString("0.#", Inv) + " мин";
		return sec.ToString(Inv) + " сек";
	}

	private static DateTime BanUntil(string ip)
	{
		lock (_sync)
		{
			DateTime t;
			if (_failUntil.TryGetValue(ip, out t))
			{
				return t;
			}
			return DateTime.MinValue;
		}
	}

	// Состояние заглушки конкретного устройства: уровень, серия, остаток паузы (сек).
	public static void GetFailState(string ip, out int level, out int series, out int waitSec)
	{
		lock (_sync)
		{
			_failLevel.TryGetValue(ip, out level);
			_failSeries.TryGetValue(ip, out series);
			DateTime t;
			waitSec = 0;
			if (_failUntil.TryGetValue(ip, out t) && t > DateTime.Now)
			{
				waitSec = (int)Math.Ceiling((t - DateTime.Now).TotalSeconds);
			}
		}
	}

	// Сводка по ВСЕМ устройствам — нужна для ручного сброса (сбрасываем всё скопом,
	// оператор не обязан знать IP телефона судьи). Возвращает максимальный уровень.
	public static int GetFailStateAll(out int devices, out int maxWaitSec)
	{
		lock (_sync)
		{
			int lvl = 0;
			devices = 0;
			maxWaitSec = 0;
			foreach (System.Collections.Generic.KeyValuePair<string, int> kv in _failLevel)
			{
				devices++;
				if (kv.Value > lvl)
				{
					lvl = kv.Value;
				}
			}
			foreach (System.Collections.Generic.KeyValuePair<string, DateTime> kv2 in _failUntil)
			{
				if (kv2.Value > DateTime.Now)
				{
					int w = (int)Math.Ceiling((kv2.Value - DateTime.Now).TotalSeconds);
					if (w > maxWaitSec)
					{
						maxWaitSec = w;
					}
				}
			}
			return lvl;
		}
	}

	// Неудачная попытка входа: уровень и серия растут, назначается пауза,
	// запись уходит в журнал (remote_com_log.txt) и в отдельный login_fails.txt.
	private static void RegisterFail(string ip, string device)
	{
		int level = 0;
		int series = 0;
		int sec = 0;
		lock (_sync)
		{
			_failLevel.TryGetValue(ip, out level);
			level++;
			_failLevel[ip] = level;
			_failSeries.TryGetValue(ip, out series);
			series++;
			_failSeries[ip] = series;
			_failLast[ip] = DateTime.Now;
			sec = FailPauseForLevel(level);
			if (sec > 0)
			{
				_failUntil[ip] = DateTime.Now.AddSeconds((double)sec);
			}
		}
		// Записи вне lock: файловый ввод не держит блокировку сервера.
		SerialPortLog.Write("WEB: неверный пароль, IP " + ip + " — попытка " + series.ToString(Inv)
			+ ", уровень " + level.ToString(Inv) + ", пауза " + FmtPause(sec));
		LoginFailLog.Write(device, ip);
	}

	private static string FmtBan(int sec)
	{
		if (sec >= 86400) return "24 часа";
		if (sec >= 3600) return (sec / 3600).ToString(Inv) + " ч";
		if (sec >= 60) return (sec / 60).ToString(Inv) + " мин";
		return sec.ToString(Inv) + " сек";
	}

	// Периодическая «уборка» — вызывается из HandleContext примерно раз в 60 секунд:
	// истёкшие сессии, закончившиеся баны и СЕРИИ неудач (IP без неудач 24 ч).
	// ВАЖНО (пункт 3): уровень паузы _failLevel уборкой НЕ снимается — только явным сбросом.
	public static void HouseKeep()
	{
		PurgeExpired();
		lock (_sync)
		{
			System.Collections.Generic.List<string> gone = new System.Collections.Generic.List<string>();
			foreach (System.Collections.Generic.KeyValuePair<string, DateTime> kv in _bans)
			{
				if (kv.Value <= DateTime.Now) gone.Add(kv.Key);
			}
			foreach (string k in gone) _bans.Remove(k);
			gone.Clear();
			foreach (System.Collections.Generic.KeyValuePair<string, DateTime> kv in _failLast)
			{
				if (kv.Value <= DateTime.Now.AddHours(-24.0)) gone.Add(kv.Key);
			}
			foreach (string k in gone)
			{
				_failSeries.Remove(k);
				_failLast.Remove(k);
				_failUntil.Remove(k);
			}
		}
	}

	// Успешный вход: серия обнуляется, пауза снимается, УРОВЕНЬ остаётся (пункт 3).
	private static void ClearFails(string ip)
	{
		lock (_sync)
		{
			_failSeries.Remove(ip);
			_failUntil.Remove(ip);
		}
	}

	// Явный ручной сброс (веб-панель с повышенными правами / меню разработчика):
	// снимает и серии, и паузы, и УРОВНИ по всем устройствам.
	// Админские баны (_bans) не трогаются — это отдельная мера, её снимает только срок.
	public static int ResetLoginFails()
	{
		int n;
		lock (_sync)
		{
			n = _failLevel.Count;
			_failSeries.Clear();
			_failLevel.Clear();
			_failUntil.Clear();
			_failLast.Clear();
		}
		SerialPortLog.Write("WEB: счётчик неудачных попыток входа сброшен вручную (устройств в списке было: " + n.ToString(Inv) + ")");
		CrashLog.Append("SECURITY", "счётчик неудачных попыток веб-входа сброшен вручную (устройств: " + n.ToString(Inv) + ")");
		return n;
	}
 	private static readonly System.Collections.Generic.Dictionary<string, DateTime> _bans = new System.Collections.Generic.Dictionary<string, DateTime>();

 	private static void PurgeBans()
 	{
 		lock (_sync)
 		{
 			System.Collections.Generic.List<string> gone = new System.Collections.Generic.List<string>();
 			foreach (System.Collections.Generic.KeyValuePair<string, DateTime> kv in _bans)
 			{
 				if (kv.Value <= DateTime.Now)
 				{
 					gone.Add(kv.Key);
 				}
 			}
 			foreach (string k in gone)
 			{
 				_bans.Remove(k);
 			}
 		}
 	}

 	private static bool BanActive(string ip)
 	{
 		lock (_sync)
 		{
 			DateTime t;
 			if (_bans.TryGetValue(ip, out t))
 			{
 				return t > DateTime.Now;
 			}
 			return false;
 		}
 	}

 	private static string JEsc(string s)
 	{
 		if (s == null)
 		{
 			return "";
 		}
 		System.Text.StringBuilder sb = new System.Text.StringBuilder();
 		foreach (char c in s)
 		{
 			if (c == '\\' || c == '"')
 			{
 				sb.Append('\\').Append(c);
 			}
 			else if (c < ' ')
 			{
 				sb.Append(' ');
 			}
 			else
 			{
 				sb.Append(c);
 			}
 		}
 		return sb.ToString();
 	}

		private static void Loop()
		{
			while (true)
			{
				System.Net.HttpListener l = _listener;
				if (l == null || !l.IsListening)
				{
					break;
				}
				System.Net.HttpListenerContext ctx = null;
				try
				{
					ctx = l.GetContext();
				}
				catch
				{
					break;
				}
				// БЛОК 3, F2-09: КАЖДЫЙ запрос обрабатывается в своём потоке из пула.
				// Раньше один поток обслуживал запросы по очереди: один "зависший" клиент
				// (телефон уснул посреди ответа) блокировал панель и остальные устройства.
				// Общее состояние (сессии, кэш) защищено lock, команды на форму уходят
				// через BeginInvoke - многопоточность здесь безопасна.
				System.Net.HttpListenerContext ctxCopy = ctx;
				System.Threading.ThreadPool.QueueUserWorkItem(new System.Threading.WaitCallback(HandleContext), ctxCopy);
			}
			SerialPortLog.Write("WEB: поток слушателя завершился");
		}

		private static void HandleContext(object stateObj)
		{
			System.Net.HttpListenerContext ctx = (System.Net.HttpListenerContext)stateObj;
			try
			{
				// БЛОК 4, F7-06: уборка устаревших сессий/банов/счётчиков ~раз в 60 с.
				// Гонка потоков безвредна: максимум уборка выполнится дважды подряд.
				if ((DateTime.Now - _lastHouseKeep).TotalSeconds > 60.0)
				{
					_lastHouseKeep = DateTime.Now;
					HouseKeep();
				}
				string url = ctx.Request.Url.AbsolutePath;
				string body = "";
				string ctype = "text/html; charset=utf-8";
				if (url == "/state.json")
					{
						// БЛОК 3, Q15: полный state теперь ТОЛЬКО для вошедших (token).
						// Публичная витрина /display читает урезанный /pub.json.
						// Сессию этот запрос НЕ продлевает (Q17: 30 мин без КОМАНД = выход).
						ctype = "application/json; charset=utf-8";
						WebSession ss;
						if (!Auth(ctx, out ss))
						{
							ctx.Response.StatusCode = 403;
							body = "{\"ok\":0}";
						}
						else
						{
							body = (StateCache == null || StateCache.Length == 0) ? "{}" : StateCache;
						}
					}
					else if (url == "/pub.json")
					{
						// БЛОК 3, Q15: публичные данные витрины: счёт, время, период,
						// названия, удаления, таймауты. Служебных полей здесь нет.
						ctype = "application/json; charset=utf-8";
						body = (PubCache == null || PubCache.Length == 0) ? "{}" : PubCache;
					}
				else if (url == "/" || url == "/display")
				{
					body = DisplayHtml();
				}
				else if (url == "/passstate")
				{
					ctype = "application/json; charset=utf-8";
					body = "{\"set\":" + (((PassHash != null && PassHash.Length > 0) ? 1 : 0)).ToString() + "}";
				}
				 				else if (url == "/login")
 				{
 					ctype = "application/json; charset=utf-8";
 					string cip = "-";
 					try
 					{
 						cip = ctx.Request.RemoteEndPoint.Address.ToString();
 					}
 					catch
 					{
 					}
 					PurgeBans();
 					// ПУНКТ 3: имя устройства (User-Agent) пишется в отдельный журнал login_fails.txt.
 					string devName = "";
 					try { devName = ctx.Request.Headers["User-Agent"] ?? ""; } catch { }
 					// Админский бан устройства (30 мин) действует для ВСЕХ, включая супер-пароль:
 					// это отдельная мера, а не «временная заглушка» из пункта 3.
 					if (BanActive(cip))
 					{
 						ctx.Response.StatusCode = 403;
 						body = "{\"error\":5}";
 					}
 					else if (ctx.Request.ContentLength64 > 4096)
 					{
 						ctx.Response.StatusCode = 413;
 						body = "{\"error\":3}";
 					}
 					else
 					{
 						string raw = "";
 						try
 						{
 							using (System.IO.StreamReader sr = new System.IO.StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
 							{
 								raw = sr.ReadToEnd();
 							}
 						}
 						catch
 						{
 						}
 						string pass = "";
 						// БЛОК 4, F3-15: мёртвый код удалён — параметр "rem" никогда не использовался
 						// (браузер сам решает, запоминать вход через localStorage).
 						foreach (string part in raw.Split(new char[] { '&' }))
 						{
 							int eq = part.IndexOf('=');
 							if (eq < 0)
 							{
 								continue;
 							}
 							string k = part.Substring(0, eq);
 							string v = System.Uri.UnescapeDataString(part.Substring(eq + 1));
 							if (k == "pass")
 							{
 								pass = v;
 							}
 						}
 						bool isSuper = DevGuard.IsSuper(pass);
 						// ============ ПУНКТ 3: НАРАСТАЮЩАЯ ПАУЗА ПОСЛЕ НЕУДАЧНЫХ ПОПЫТОК ============
 						// Если пауза сработала — обычный вход отклоняется СРАЗУ: пароль даже не проверяется
 						// и новая пауза не назначается. Вход с повышенными правами (супер-пароль) проходит
 						// БЕЗ ограничений — осознанное решение для удобства оператора.
 						// Клиенту отдаётся реальный остаток паузы, текущий уровень и длина серии.
 						bool failBlocked = (!isSuper) && DateTime.Now < BanUntil(cip);
 						if (failBlocked)
 						{
 							int waitSec = (int)Math.Ceiling((BanUntil(cip) - DateTime.Now).TotalSeconds);
 							if (waitSec < 1) { waitSec = 1; }
 							int lvlNow = 0; int serNow = 0; int waitChk = 0;
 							GetFailState(cip, out lvlNow, out serNow, out waitChk);
 							ctx.Response.StatusCode = 403;
 							body = "{\"error\":4,\"wait\":" + waitSec.ToString(Inv)
 								+ ",\"level\":" + lvlNow.ToString(Inv)
 								+ ",\"series\":" + serNow.ToString(Inv) + "}";
 						}
 						// БЛОК 4, F7-03: проверка пароля через PBKDF2 (старый SHA-1 тоже
 						// принимается и при успехе прозрачно заменяется на PBKDF2).
 						bool legacyHash = false;   // инициализация обязательна: VerifyPassword может не вызваться
 						bool okPass = (!failBlocked) && PassHash != null && PassHash.Length > 0
 							&& (VerifyPassword(pass, PassHash, out legacyHash) || isSuper);
 						if (okPass && legacyHash && !isSuper && PassHashUpgraded != null)
 						{
 							try
 							{
 								string upgraded = HashPassword(pass);
 								PassHash = upgraded;
 								PassHashUpgraded(upgraded);
 								SerialPortLog.Write("БЕЗОПАСНОСТЬ: пароль веб-оператора переведён с SHA-1 на PBKDF2");
 							}
 							catch (Exception exUp)
 							{
 								Diag.Note("Апгрейд WebPassHash", exUp);
 							}
 						}
 						PurgeExpired();
 						if (okPass)
 						{
 							ClearFails(cip);
 							string finger = FingerOf(ctx);
 							lock (_sync)
 							{
 								System.Collections.Generic.List<string> same = new System.Collections.Generic.List<string>();
 								foreach (System.Collections.Generic.KeyValuePair<string, WebSession> kv in _tokens)
 								{
 									if (kv.Value.Finger == finger)
 									{
 										same.Add(kv.Key);
 									}
 								}
 								foreach (string k in same)
 								{
 									_tokens.Remove(k);
 								}
 								int regular = 0;
 								foreach (System.Collections.Generic.KeyValuePair<string, WebSession> kv in _tokens)
 								{
 									if (!kv.Value.Super)
 									{
 										regular++;
 									}
 								}
 								if (!isSuper && regular >= MaxDevices)
 								{
 									ctx.Response.StatusCode = 403;
 									body = "{\"error\":2}";
 								}
 								else
 								{
 									WebSession ns = new WebSession();
 									ns.Token = System.Guid.NewGuid().ToString("N");
 									ns.Finger = finger;
 									ns.LastCmd = DateTime.Now;
 									ns.Created = DateTime.Now;   // БЛОК 4, F7-07: TTL 8 часов
 									ns.Sid = ns.Token.Substring(0, 8);   // БЛОК 4, F7-11: публичный идентификатор
 									ns.Super = isSuper;
 									ns.Ip = cip;
 									ns.Ua = ctx.Request.Headers["User-Agent"] ?? "";
 									_tokens[ns.Token] = ns;
 									body = "{\"token\":\"" + ns.Token + "\"}";
 								}
 							}
 						}
 						// ПУНКТ 3: неудача регистрируется, только если пароль действительно проверялся.
 						// При сработавшей заглушке ответ уже сформирован выше (error 4) — счётчик не растёт.
 						else if (!failBlocked)
 						{
 							RegisterFail(cip, devName);
 							ctx.Response.StatusCode = 403;
 							body = "{\"error\":1}";
 						}
 					}
 				}
				else if (url == "/sessioncheck")
				{
					ctype = "application/json; charset=utf-8";
					WebSession s;
					if (Auth(ctx, out s))
					{
						body = "{\"ok\":1,\"super\":" + (s.Super ? "1" : "0") + "}";
					}
					else
					{
						ctx.Response.StatusCode = 403;
						body = "{\"ok\":0}";
					}
				}
				else if (url == "/logout")
				{
					ctype = "application/json; charset=utf-8";
					// БЛОК 4, F7-07: токен из заголовка, ?token= — запасной вариант.
					string tok = ctx.Request.Headers["X-Tablo-Token"];
					if (string.IsNullOrEmpty(tok))
					{
						tok = ctx.Request.QueryString["token"];
					}
					lock (_sync)
					{
						if (tok != null)
						{
							_tokens.Remove(tok);
						}
					}
					body = "{\"ok\":1}";
				}
				else if (url == "/cmd")
				{
					ctype = "application/json; charset=utf-8";
					WebSession s;
					if (!Auth(ctx, out s))
					{
						ctx.Response.StatusCode = 403;
						body = "{\"ok\":0}";
					}
					else
					{
						string key = ctx.Request.QueryString["key"];
						int p = 0;
						try
						{
							p = int.Parse(ctx.Request.QueryString["p"] ?? "0");
						}
						catch
						{
						}
						// БЛОК 3, F3-09: честный результат: 0=выполнено, 1=неизвестная команда,
							// 2=отклонено защитой (идёт время периода), 3=сбой. Плюс аудит в лог.
							int rc = 3;
							if (CmdProvider != null)
							{
								try
								{
									rc = CmdProvider(key, p);
								}
								catch (Exception exc)
								{
									rc = 3;
									SerialPortLog.Write("WEB: исключение при выполнении команды " + key + ": " + exc.Message);
								}
							}
							Touch(s);
							SerialPortLog.Write("WEB CMD: ip=" + s.Ip + " key=" + (key ?? "-") + " p=" + p.ToString() + " -> " + ((rc == 0) ? "выполнено" : ("отказ, код " + rc.ToString())));
							body = (rc == 0) ? "{\"ok\":1}" : ("{\"ok\":0,\"err\":" + rc.ToString() + "}");
					}
				}
				else if (url == "/cmdtext")
				{
					ctype = "application/json; charset=utf-8";
					WebSession s;
					if (!Auth(ctx, out s))
					{
						ctx.Response.StatusCode = 403;
						body = "{\"ok\":0}";
					}
					else
					{
						string key = ctx.Request.QueryString["key"];
						string v = ctx.Request.QueryString["v"] ?? "";
						// БЛОК 4, F3-11: Regex.Unescape раскрывал ЛЮБЫЕ escape-последовательности
						// (включая \n, \t и мусор) — заменён на ограниченный декодер: только
						// «обратная косая + |», двойная обратная косая и юникод-эскейпы u+4hex
						// (управляющие символы не пропускаются).
						v = UnescapeText(v);
						bool ok = false;
						if (CmdTextProvider != null)
						{
							try
							{
								ok = CmdTextProvider(key, v);
							}
							catch (Exception exTxt)
							{
								// БЛОК 4, F8-03: исключение обработчика больше не проглатывается молча
								ok = false;
								SerialPortLog.Write("WEB: исключение при обработке текстовой команды " + (key ?? "-") + ": " + exTxt.Message);
							}
						}
						Touch(s);
							// БЛОК 3, F3-10: аудит текстовых команд (названия, эталон)
							string vlog = (v.Length > 80) ? (v.Substring(0, 80) + "...") : v;
							SerialPortLog.Write("WEB TEXT: ip=" + s.Ip + " key=" + (key ?? "-") + " v=" + vlog + " -> " + (ok ? "выполнено" : "отклонено"));
							body = ok ? "{\"ok\":1}" : "{\"ok\":0}";
					}
				}
				else if (url == "/adminfail" || url == "/adminresetfails")
				{
					// ================== ПУНКТ 3: РУЧНОЙ СБРОС СЧЁТЧИКА ПОПЫТОК ВХОДА ==================
					// Только для сессии с повышенными правами (супер-пароль).
					// /adminfail       — текущее состояние: максимальный уровень паузы, число устройств,
					//                    остаток самой длинной паузы.
					// /adminresetfails — сброс; ОБЯЗАТЕЛЬНО подтверждение повторным вводом значения:
					//                    ?confirm=N, где N — уровень, показанный панелью. Не совпал ->
					//                    сброс не выполняется и панель получает актуальное значение.
					ctype = "application/json; charset=utf-8";
					WebSession sf;
					if (!Auth(ctx, out sf) || !sf.Super)
					{
						ctx.Response.StatusCode = 403;
						body = "{\"ok\":0}";
					}
					else
					{
						Touch(sf);
						int devCnt = 0;
						int maxWait = 0;
						int lvl = GetFailStateAll(out devCnt, out maxWait);
						if (url == "/adminfail")
						{
							body = "{\"ok\":1,\"level\":" + lvl.ToString(Inv)
								+ ",\"devices\":" + devCnt.ToString(Inv)
								+ ",\"wait\":" + maxWait.ToString(Inv)
								+ ",\"pause\":" + FailPauseForLevel(lvl + 1).ToString(Inv) + "}";
						}
						else
						{
							string conf = ctx.Request.QueryString["confirm"] ?? "";
							if (conf.Trim() != lvl.ToString(Inv))
							{
								SerialPortLog.Write("WEB: АДМИН - сброс счётчика попыток ОТКЛОНЕН: подтверждение \""
									+ conf + "\" не совпало с текущим уровнем " + lvl.ToString(Inv));
								body = "{\"ok\":0,\"level\":" + lvl.ToString(Inv) + ",\"confirm\":0}";
							}
							else
							{
								int n = ResetLoginFails();
								SerialPortLog.Write("WEB: АДМИН - счётчик неудачных попыток входа сброшен (ip=" + sf.Ip + ")");
								body = "{\"ok\":1,\"reset\":" + n.ToString(Inv) + ",\"level\":0}";
							}
						}
					}
				}
                     				else if (url == "/admin" || url == "/adminkick" || url == "/adminban")
 				{
 					ctype = "application/json; charset=utf-8";
 					WebSession s;
 					if (!Auth(ctx, out s) || !s.Super)
 					{
 						ctx.Response.StatusCode = 403;
 						body = "{\"ok\":0}";
 					}
 					else if (url == "/admin")
 					{
 						System.Text.StringBuilder sb = new System.Text.StringBuilder();
 						sb.Append("[");
 						lock (_sync)
 						{
 							bool first = true;
 							foreach (System.Collections.Generic.KeyValuePair<string, WebSession> kv in _tokens)
 							{
 								if (!first)
 								{
 									sb.Append(",");
 								}
 								first = false;
 								int idle = (int)(DateTime.Now - kv.Value.LastCmd).TotalMinutes;
 								// БЛОК 4, F7-11: полный токен больше НЕ отдаётся в браузер — только
 								// короткий идентификатор sid (8 символов) и признак «это моя сессия».
 								sb.Append("{\"sid\":\"" + JEsc(kv.Value.Sid) + "\",\"ip\":\"" + JEsc(kv.Value.Ip) + "\",\"ua\":\"" + JEsc(kv.Value.Ua) + "\",\"super\":" + (kv.Value.Super ? "1" : "0") + ",\"me\":" + ((kv.Key == s.Token) ? "1" : "0") + ",\"idle\":" + idle.ToString() + "}");
 							}
 						}
 						sb.Append("]");
 						body = sb.ToString();
 					}
 					else
 					{
 						string target = ctx.Request.QueryString["target"] ?? "";
 						bool done = false;
 						lock (_sync)
 						{
 							// БЛОК 4, F7-11: target — это sid (короткий идентификатор) или,
 							// для совместимости, полный токен.
 							string realKey = null;
 							WebSession ts = null;
 							if (_tokens.ContainsKey(target))
 							{
 								realKey = target;
 								ts = _tokens[target];
 							}
 							else
 							{
 								foreach (System.Collections.Generic.KeyValuePair<string, WebSession> kv2 in _tokens)
 								{
 									if (kv2.Value.Sid == target)
 									{
 										realKey = kv2.Key;
 										ts = kv2.Value;
 										break;
 									}
 								}
 							}
 							if (ts != null && realKey != s.Token)
 							{
 								if (url == "/adminban")
 								{
 									if (ts.Ip != s.Ip)
 									{
 										_bans[ts.Ip] = DateTime.Now.AddMinutes(30);
 										SerialPortLog.Write("WEB: АДМИН - бан устройства " + ts.Ip + " на 30 минут");
 										_tokens.Remove(realKey);
 										done = true;
 									}
 								}
 								else
 								{
 									SerialPortLog.Write("WEB: АДМИН - кик сессии устройства " + ts.Ip);
 									_tokens.Remove(realKey);
 									done = true;
 								}
 							}
 						}
 						body = done ? "{\"ok\":1}" : "{\"ok\":0}";
 					}
 				}
                else if (url == "/panel")
 				{
 					body = PanelHtml();
 				}
				else if (url == "/operator")
				{
					body = OperatorHtml();
				}
				else
				{
					ctx.Response.StatusCode = 404;
					body = "not found";
				}
								byte[] buf = System.Text.Encoding.UTF8.GetBytes(body);
				ctx.Response.ContentType = ctype;
				// БЛОК 4, F7-09: единые заголовки безопасности для всех ответов.
				// БЛОК 4, F3-15: HTML-страницам разрешена ревалидация (no-cache — панель
				// после обновления страницы не «залипает» на старой копии), данные — no-store.
				bool isHtml = ctype.StartsWith("text/html");
				AddSecurityHeaders(ctx, isHtml);
				try
				{
					ctx.Response.AddHeader("Cache-Control", isHtml ? "no-cache, must-revalidate" : "no-store, must-revalidate");
					ctx.Response.AddHeader("Pragma", "no-cache");
				}
				catch
				{
				}
				ctx.Response.ContentLength64 = buf.Length;
				ctx.Response.OutputStream.Write(buf, 0, buf.Length);
				ctx.Response.OutputStream.Close();
			}
			catch
			{
			}
			finally
			{
				// БЛОК 3, F2-09: ответ закрывается ВСЕГДА, иначе клиент "висит" до таймаута
				try
				{
					ctx.Response.Close();
				}
				catch
				{
				}
			}
		}

				private static string DisplayHtml()
	{
		return @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Tablo</title>
<style>
html,body{margin:0;background:#000;color:#fff;font-family:Consolas,'Courier New',monospace;font-weight:bold;overflow:hidden;}
#stage{position:absolute;left:50%;top:50%;width:240px;height:160px;margin-left:-120px;margin-top:-80px;-webkit-transform-origin:50% 50%;transform-origin:50% 50%;}
.a{position:absolute;text-align:center;white-space:pre;}
#nh{left:8px;top:8px;width:112px;height:36px;font-size:18px;line-height:18px;}
#na{left:120px;top:8px;width:112px;height:36px;font-size:18px;line-height:18px;}
#sh{left:8px;top:52px;width:48px;height:32px;font-size:32px;line-height:32px;}
#sa{left:184px;top:52px;width:48px;height:32px;font-size:32px;line-height:32px;}
#time{left:68px;top:52px;width:104px;height:32px;font-size:32px;line-height:32px;}
#time.two{height:16px;font-size:16px;line-height:16px;}
#time2{left:68px;top:68px;width:104px;height:16px;font-size:16px;line-height:16px;}
#period{left:108px;top:96px;width:24px;height:24px;font-size:24px;line-height:24px;}
#ph{left:8px;top:112px;width:87px;height:32px;font-size:16px;line-height:16px;text-align:left;}
#pa{left:145px;top:112px;width:87px;height:32px;font-size:16px;line-height:16px;text-align:left;}
#dots{left:68px;top:124px;width:104px;height:16px;font-size:14px;line-height:16px;}
#splash{position:absolute;left:0;top:0;width:240px;height:160px;display:none;}
#clock{left:5px;top:10px;width:230px;height:40px;font-size:40px;line-height:40px;}
#sdate{left:5px;top:60px;width:230px;height:16px;font-size:16px;line-height:16px;text-align:center;}
</style>
</head>
<body>
<div id='stage'>
<div id='sb'>
<div class='a' id='nh'></div>
<div class='a' id='na'></div>
<div class='a' id='sh'>0</div>
<div class='a' id='time'>--:--</div>
<div class='a' id='time2'></div>
<div class='a' id='sa'>0</div>
<div class='a' id='period'>1</div>
<div class='a' id='ph'></div>
<div class='a' id='pa'></div>
<div class='a' id='dots'></div>
</div>
<div id='splash'><div class='a' id='clock'>--:--:--</div><div class='a' id='sdate'></div></div>
</div>
<script>
function pens(s){var out='';var arr=(''+(s||'')).split('; ');for(var i=0;i<arr.length&&i<2;i++){if(arr[i])out+=arr[i]+'\n';}return out;}
function dots(n){var s='';for(var i=0;i<(n||0);i++)s+='\u25CF ';return s;}
function fit(){var s=Math.min(window.innerWidth/240,window.innerHeight/160);
var st=document.getElementById('stage');var tr='scale('+s+')';
st.style.transform=tr;if('webkitTransform' in st.style){st.style.webkitTransform=tr;}}
window.onresize=fit;window.onorientationchange=function(){setTimeout(fit,200);};fit();
var MO=['января','февраля','марта','апреля','мая','июня','июля','августа','сентября','октября','ноября','декабря'];
var WD=['ВС','ПН','ВТ','СР','ЧТ','ПТ','СБ'];
function dstr(){var d=new Date();return ('0'+d.getDate()).slice(-2)+' '+MO[d.getMonth()]+' /'+WD[d.getDay()]+'/';}
function tick(){var x=new XMLHttpRequest();x.open('GET','/pub.json?ts='+new Date().getTime(),true);
x.onload=function(){setTimeout(tick,500);
if(x.status!=200){return;}
var s=null;try{s=JSON.parse(x.responseText);}catch(e){s=null;}
if(!s){return;}
var splash=(s.mode=='CLK'||s.mode=='PRE');
document.getElementById('sb').style.display=splash?'none':'block';
document.getElementById('splash').style.display=splash?'block':'none';
if(splash){
document.getElementById('clock').textContent=s.value||'--:--:--';
document.getElementById('sdate').textContent=dstr();
}else{
document.getElementById('nh').textContent=(s.name_h||'')+'\n'+(s.town_h||'');
document.getElementById('na').textContent=(s.name_a||'')+'\n'+(s.town_a||'');
document.getElementById('sh').textContent=(s.score_h!=null?s.score_h:0);
document.getElementById('sa').textContent=(s.score_a!=null?s.score_a:0);
var tEl=document.getElementById('time');
if(s.value2){tEl.className='a two';}else{tEl.className='a';}
tEl.textContent=s.value||'--:--';
document.getElementById('time2').textContent=s.value2||'';
document.getElementById('period').textContent=(s.period!=null?s.period:1);
document.getElementById('dots').textContent=dots(s.to_h)+'  '+dots(s.to_a);
document.getElementById('ph').textContent=pens(s.pen_h);
document.getElementById('pa').textContent=pens(s.pen_a);}
};
x.onerror=function(){setTimeout(tick,2000);};
x.send();}
tick();
</script>
</body>
</html>";
	}
	private static string PanelHtml()
	{
		return @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Пульт оператора</title>
<style>
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent;}
html,body{margin:0;background:#101010;color:#eee;font-family:Consolas,'Courier New',monospace;}
button{background:#222;color:#eee;border:1px solid #555;border-radius:6px;font-size:15px;padding:10px 6px;cursor:pointer;font-family:inherit;}
button:hover{background:#2e2e2e;}
button:active{background:#3a3a3a;transform:translateY(1px);}
button.warn{background:#7a2020;border-color:#c66;}
input.txt{background:#222;color:#eee;border:1px solid #666;border-radius:6px;font-size:15px;padding:8px;width:100%;font-family:inherit;}
#login{margin:10vh auto;width:300px;text-align:center;padding:0 10px;}
#lmsg{color:#f88;min-height:20px;margin-top:8px;}
#ui{display:none;padding:8px;max-width:1400px;margin:0 auto;}
.top{display:grid;grid-template-columns:1fr 1fr 1fr;grid-gap:8px;gap:8px;margin-bottom:8px;}
.top button{font-size:16px;padding:10px 4px;}
.grid{display:grid;grid-template-columns:1fr 1.25fr 1fr;grid-gap:8px;gap:8px;}
.col,.center{display:flex;flex-direction:column;}
.col>*,.center>*{margin:3px 0;}
.row{display:grid;grid-template-columns:1fr 1fr;grid-gap:8px;gap:8px;}
/* ПУНКТ 10: пять быстрых шаблонов (+1,5 / +2 / +4 / +5 / Своё) В ОДНУ СТРОКУ,
   сами кнопки уменьшены. Если в один ряд не помещается - уберите +4: для этого
   достаточно вернуть для .p4 display:none (см. ниже) и 4 колонки вместо 5. */
.row3{display:grid;grid-template-columns:1fr 1fr 1fr 1fr 1fr;grid-gap:4px;gap:4px;}
.row3 button{font-size:12px;padding:8px 1px;}
.score{background:#000;border:1px solid #444;border-radius:6px;text-align:center;font-size:40px;line-height:1.05;padding:2px 4px;}
.cap{text-align:center;font-size:11px;color:#9a9a9a;margin-top:2px;}
.time{background:#000;border:1px solid #444;border-radius:6px;text-align:center;font-size:44px;line-height:1.05;padding:2px 6px;}
.time2{font-size:20px;line-height:1.2;min-height:0;color:#ffd54f;text-align:center;}
.time2:empty{display:none;}
.fix{display:grid;grid-template-columns:1fr 1fr;grid-gap:6px;gap:6px;}
.per{display:grid;grid-template-columns:1fr 2fr 1fr;grid-gap:6px;gap:6px;align-items:stretch;}
/* ПУНКТ 9: элемент «ПЕРИОД n» того же размера, что и кнопка «СНЯТЬ ШТРАФ»
   (база: font-size:15px; padding:10px 6px). Строка стала ниже - все кнопки
   средней колонки ниже неё поднялись вверх. */
.per div{background:#000;border:1px solid #444;border-radius:6px;text-align:center;font-size:15px;padding:10px 6px;box-sizing:border-box;display:flex;align-items:center;justify-content:center;}
.row3 .p4{display:block;}   /* ПУНКТ 10: +4 теперь виден и на широком экране */
#bBack,#bBrkStop{font-size:13px;padding:8px;}
#ssBtn{font-size:20px;padding:12px 6px;background:#000;border:1px solid #777;color:#fff;border-radius:8px;}
.pens{background:#181818;border:1px solid #333;border-radius:6px;padding:4px;min-height:40px;font-size:12px;}
.pens div{padding:2px 4px;border-bottom:1px dashed #333;}
.badge{font-size:10px;color:#9a9a9a;text-align:center;min-height:14px;}
#toast{position:fixed;left:50%;bottom:18px;transform:translateX(-50%);background:#222;border:1px solid #555;border-radius:6px;padding:10px 18px;font-size:15px;display:none;z-index:60;max-width:90vw;}
#toast.ok{border-color:#4c4;color:#cfc;}
#toast.err{border-color:#c44;color:#fcc;}
.modal{position:fixed;left:0;top:0;right:0;bottom:0;background:rgba(0,0,0,0.65);display:none;align-items:center;justify-content:center;z-index:50;}
.modal.on{display:flex;}
.box{background:#1b1b1b;border:1px solid #555;border-radius:8px;padding:16px;width:330px;max-width:92vw;}
.box h3{margin:0 0 10px;font-size:18px;}
.box .err{color:#f88;font-size:13px;min-height:18px;margin:6px 0;}
.acts{display:grid;grid-template-columns:1fr 1fr;grid-gap:10px;gap:10px;margin-top:10px;}
@media (max-width:1100px){
#ui{padding:6px;}
button{font-size:13px;padding:8px 4px;}
.top{grid-gap:6px;gap:6px;margin-bottom:6px;}
.top button{font-size:13px;padding:8px 2px;}
.grid{grid-gap:6px;gap:6px;}
.col>*,.center>*{margin:2px 0;}
.row{grid-gap:6px;gap:6px;}
.row3{grid-gap:4px;gap:4px;}
.score{font-size:32px;}
.time{font-size:34px;}
.time2{font-size:16px;}
.cap{font-size:9px;}
.badge{font-size:9px;}
input.txt{font-size:13px;padding:6px;}
.per div{font-size:13px;padding:8px 4px;}
#ssBtn{font-size:17px;padding:10px 4px;}
.pens{font-size:10px;min-height:32px;}
#ssBtn{font-size:26px;padding:15px 6px;}
.row button{font-size:16px;padding:12px 4px;}
.row3{grid-template-columns:1fr 1fr 1fr 1fr 1fr;}
.row3 button{font-size:11px;padding:8px 1px;}
.row3 .p4{display:block;}
#bBack,#bBrkStop{width:auto;align-self:center;padding:8px 3%;}
}
@media (max-width:700px){
#ui{padding:4px;}
button{font-size:11px;padding:6px 2px;min-height:44px;}
.top{grid-gap:4px;gap:4px;margin-bottom:4px;}
.top button{font-size:11px;padding:6px 2px;}
.grid{grid-gap:4px;gap:4px;}
.col>*,.center>*{margin:2px 0;}
.row{grid-gap:4px;gap:4px;}
.row3{grid-gap:3px;gap:3px;}
.score{font-size:24px;}
.time{font-size:26px;}
.time2{font-size:13px;}
.cap{font-size:8px;}
.badge{font-size:8px;}
input.txt{font-size:11px;padding:4px;}
.per div{font-size:11px;padding:6px 2px;min-height:44px;}
#ssBtn{font-size:14px;padding:8px 2px;}
.pens{font-size:9px;min-height:26px;}
#ssBtn{font-size:21px;padding:12px 4px;}
.row button{font-size:13px;padding:9px 3px;}
.row3{grid-template-columns:1fr 1fr 1fr 1fr 1fr;}
.row3 button{font-size:10px;padding:9px 1px;}
.row3 .p4{display:block;}
#bBack,#bBrkStop{width:auto;align-self:center;padding:6px 3%;}
}
@media (max-height:520px){
#ui{padding:4px;}
.top{grid-gap:4px;gap:4px;margin-bottom:4px;}
.top button{font-size:11px;padding:5px 2px;}
button{font-size:10px;padding:5px 2px;}
.grid{grid-gap:4px;gap:4px;}
.col>*,.center>*{margin:2px 0;}
.row{grid-gap:4px;gap:4px;}
.row3{grid-gap:3px;gap:3px;}
.score{font-size:20px;}
.time{font-size:22px;}
.time2{font-size:12px;}
.cap{font-size:8px;}
.badge{font-size:8px;}
input.txt{font-size:10px;padding:3px;}
.per div{font-size:10px;padding:5px 2px;}
#ssBtn{font-size:13px;padding:6px 2px;}
.pens{font-size:9px;min-height:22px;}
#ssBtn{font-size:20px;padding:10px 4px;}
.row button{font-size:12px;padding:8px 3px;}
.row3{grid-template-columns:1fr 1fr 1fr 1fr 1fr;}
.row3 button{font-size:9px;padding:6px 1px;}
.row3 .p4{display:block;}
#bBack,#bBrkStop{width:auto;align-self:center;padding:5px 3%;}
}
</style>
</head>
<body>
<div id='login'>
<h3>Пульт оператора</h3>
<input type='password' id='pass' placeholder='Пароль'>
<button id='lg' style='width:100%;margin-top:8px;font-size:18px;padding:12px;'>Войти</button>
<div id='lmsg'></div>
</div>
<div id='ui'>
<div class='top'>
<button id='bBreak'>ПЕРЕРЫВ</button>
<button id='bReset' class='warn'>МАТЧ-СБРОС</button>
<button id='bSiren'>СИРЕНА</button>
</div>
<div class='grid'>
<div class='col'>
<div class='row'><button id='hGp'>Г+</button><button id='hGm'>Г-</button></div>
<div><div class='score' id='sH'>0</div><div class='cap'>Счёт хозяев</div></div>
<div class='row'><button id='hTp'>Т+</button><button id='hTm'>Т-</button></div>
<div class='badge' id='hTo'>таймаутов: 0</div>
<div class='row3'><button id='hP15'>+1,5</button><button id='hP2'>+2</button><button id='hP4' class='p4'>+4</button><button id='hP5'>+5</button><button id='hPc'>Своё</button></div>
<button id='hRem'>СНЯТЬ ШТРАФ</button>
<div class='pens' id='pH'><div>нет удалений</div></div>
</div>
<div class='center'>
<div><div class='time' id='tV'>--:--</div><div class='time2' id='tV2'></div></div>
<div class='fix'><input class='txt' id='fixIn' placeholder='MM:SS коррекция времени' inputmode='numeric' pattern='[0-9]*'><button id='fixOk'>ПОДТВЕРДИТЬ</button></div> 
<div class='badge' id='refShow' style='min-height:18px;text-align:left;'>Эталон: -</div>
<div class='fix'><input class='txt' id='rm' placeholder='MM:SS' inputmode='numeric' pattern='[0-9]*'><input class='txt' id='ro' placeholder='MM:SS' inputmode='numeric' pattern='[0-9]*'></div>
<div class='row'><button id='bRefRemember'>ЗАПОМНИТЬ</button><button id='bRefEdit'>ПРАВИТЬ</button></div>
<div class='per'><button id='pPrev'>-</button><div id='pV'>ПЕРИОД 1</div><button id='pNext'>+</button></div>
<button id='ssBtn'>СТАРТ-СТОП</button>
<button id='bBack'>Панель оператора</button>
<button id='bBrkStop'>Остановить перерыв</button>
</div>
<div class='col'>
<div class='row'><button id='aGp'>Г+</button><button id='aGm'>Г-</button></div>
<div><div class='score' id='sA'>0</div><div class='cap'>Счёт гостей</div></div>
<div class='row'><button id='aTp'>Т+</button><button id='aTm'>Т-</button></div>
<div class='badge' id='aTo'>таймаутов: 0</div>
<div class='row3'><button id='aP15'>+1,5</button><button id='aP2'>+2</button><button id='aP4' class='p4'>+4</button><button id='aP5'>+5</button><button id='aPc'>Своё</button></div>
<button id='aRem'>СНЯТЬ ШТРАФ</button>
<div class='pens' id='pA'><div>нет удалений</div></div>
</div>
</div>
</div>
<div id='toast'></div>
<div class='modal' id='mPen'><div class='box'>
<h3 id='mPenT'>Штраф</h3>
<input class='txt' id='mPenN' placeholder='Номер игрока 1-99' inputmode='numeric' pattern='[0-9]*'>
<input class='txt' id='mPenM' placeholder='Время штрафа MM:SS или минут' style='display:none;margin-top:8px;' inputmode='numeric' pattern='[0-9]*'>
<div class='err' id='mPenE'></div>
<div class='acts'><button id='mPenC'>Отмена</button><button id='mPenOk'>ДОБАВИТЬ</button></div>
</div></div>
<div class='modal' id='mRem'><div class='box'>
<h3 id='mRemT'>Снять штраф</h3>
<input class='txt' id='mRemN' placeholder='Номер игрока 1-99' inputmode='numeric' pattern='[0-9]*'>
<div class='err' id='mRemE'></div>
<div class='acts'><button id='mRemC'>Отмена</button><button id='mRemOk'>СНЯТЬ</button></div>
</div></div>
<div class='modal' id='mReset'><div class='box'>
<h3>ВНИМАНИЕ: полный сброс матча</h3>
<div style='font-size:14px;line-height:1.5;'>Обнуляются счёт, период, игровое время, все удаления и таймауты обеих команд. Нажмите СБРОС для второго подтверждения или Отмена для выхода.</div>
<div class='acts'><button id='mResetC'>Отмена</button><button id='mResetOk' class='warn'>СБРОС</button></div>
</div></div>
<script>
var token='';var RUN=false;var pendRem=null;var penTeam='h';var penMin=null;var pollFail=0;var refKey='';var refDirty=false;
function setTok(x){try{x.setRequestHeader('X-Tablo-Token',token);}catch(e){}}
function fmtWait(s){s=+s||60;if(s>=3600)return Math.round(s/3600)+' ч';if(s>=60)return Math.round(s/60)+' мин';return s+' сек';}
function el(i){return document.getElementById(i);}
function toast(m,ok){var t=el('toast');if(!t)return;t.textContent=m;t.className=ok?'ok':'err';t.style.display='block';setTimeout(function(){t.style.display='none';},3000);}
function pad2(n){return (n<10?'0':'')+n;}
function parseSec(v){
v=(''+(v==null?'':v)).trim();
if(v==='')return -1;
var parts=v.split(':');
var h=0,m=0,s=0;
if(parts.length===3){h=+parts[0];m=+parts[1];s=+parts[2];}
else if(parts.length===2){m=+parts[0];s=+parts[1];}
else if(parts.length===1){
var d=parts[0];
if(!/^\d+$/.test(d))return -1;
if(d.length===6){h=+d.substr(0,2);m=+d.substr(2,2);s=+d.substr(4,2);}
else if(d.length===4){m=+d.substr(0,2);s=+d.substr(2,2);}
else if(d.length===3){m=+d.substr(0,1);s=+d.substr(1,2);}
else if(d.length===2){m=+d;}
else if(d.length===1){m=+d;}
else return -1;
}else return -1;
if(isNaN(h)||isNaN(m)||isNaN(s))return -1;
if(s<0||s>59||m<0||m>59||h<0||h>9)return -1;
return h*3600+m*60+s;}
function readNum(v){var n=parseInt((''+v).trim(),10);if(isNaN(n)||n<1||n>99)return -1;return n;}
function fillPens(box,str){if(!box)return;box.innerHTML='';var rows=(''+(str||'')).split('; ');var any=false;
for(var i=0;i<rows.length;i++){if(!rows[i])continue;any=true;var d=document.createElement('div');d.textContent=rows[i];box.appendChild(d);}
if(!any){var d2=document.createElement('div');d2.textContent='нет удалений';box.appendChild(d2);}}
function cmd(k,p){if(!token)return;var x=new XMLHttpRequest();
x.open('GET','/cmd?key='+k+'&p='+(p||0),true);setTok(x);
x.onload=function(){if(x.status==403){relogin('Сессия завершена — войдите снова');return;}
var r={};try{r=JSON.parse(x.responseText);}catch(e){}
if(r.ok!==1){toast((r.err==2)?'Отклонено: сейчас идёт время периода':'Команда отклонена программой (код '+(r.err||0)+')',false);}};
x.onerror=function(){toast('Нет связи с программой',false);};
x.send();}
function poll(){if(!token){return;}var x=new XMLHttpRequest();x.open('GET','/state.json?ts='+new Date().getTime(),true);setTok(x);
x.onload=function(){if(x.status==403){relogin('Сессия завершена — войдите снова');return;}setTimeout(poll,500);
var raw=x.responseText;var s=null;var perr='';
try{s=JSON.parse(raw);}catch(e){perr=(''+((e&&e.message)?e.message:'неизвестная ошибка'));}
if(!s){pollFail++;if(pollFail==5){toast('Панель: не-JSON: '+perr+' | хвост: '+raw.substring(raw.length-100),false);pollFail=0;}return;}
if(!s||typeof s!=='object'){pollFail++;if(pollFail==5){toast('Панель: не-JSON, тело: '+raw.substring(0,60),false);pollFail=0;}return;}
pollFail=0;
try{el('sH').textContent=s.score_h;el('sA').textContent=s.score_a;}catch(e){}
try{el('tV').textContent=s.value||'--:--';el('tV2').textContent=s.value2||'';}catch(e){}
try{el('pV').textContent='ПЕРИОД '+(s.period||1);}catch(e){}
try{el('hTo').textContent='таймаутов: '+(s.to_h||0);el('aTo').textContent='таймаутов: '+(s.to_a||0);}catch(e){}
try{fillPens(el('pH'),s.pen_h);fillPens(el('pA'),s.pen_a);}catch(e){}
try{
var remembered=((+s.ref_main)>=0)||((+s.ref_ot)>=0);
var key=(s.ref_main)+'|'+(s.ref_ot)+'|'+(s.ref_lock);
if(refKey===''){refKey=key;}
var changed=(key!==refKey);
if(changed){refKey=key;refDirty=false;}
var rs=el('refShow');
if(rs){
if(remembered){rs.textContent='Эталон: осн '+fmtP(s.ref_main)+' | овт '+fmtP(s.ref_ot)+' | '+((s.ref_lock==1)?'БЛОК':'ОТКР');}
else{rs.textContent='Эталон: осн '+fmtP(s.game_sec)+' | овт '+fmtP(s.ot_sec)+' | НЕ ЗАПОМНЕН (текущая настройка)';}
}
if(!refDirty&&(changed||!remembered)){
var eR=el('rm'),eO=el('ro');
if(eR&&document.activeElement!==eR){eR.value=remembered?fmtP(s.ref_main):fmtP(s.game_sec);}
if(eO&&document.activeElement!==eO){eO.value=remembered?fmtP(s.ref_ot):fmtP(s.ot_sec);}
}
}catch(e){}
RUN=(s.run==1);MODE=s.mode||'';var elaps=+(s.elapsed||0);
try{el('ssBtn').style.background=RUN?'#c00000':(elaps>0?'#0055cc':'#000000');}catch(e){}
if(pendRem){pendRem.wait--;
if(pendRem.wait<=0){var list=(''+((pendRem.team=='h')?s.pen_h:s.pen_a));
var nn=pad2(pendRem.num);var has=(('; '+list).indexOf('; '+nn+' ')>=0);
if(has){toast('Игрок №'+pendRem.num+' не найден в списке удалений',false);}
else{toast('Удаление подтверждено: игрок №'+pendRem.num+' снят',true);}
pendRem=null;}}};
x.onerror=function(){setTimeout(poll,1500);pollFail++;if(pollFail==5){toast('Панель: нет связи с сервером',false);pollFail=0;}};
x.send();}
function openPen(team,min){penTeam=team;penMin=min;
/* ПУНКТ 10: дробные шаблоны (1,5 мин) показываем с запятой, как в русском интерфейсе */
el('mPenT').textContent='Штраф: '+(team=='h'?'ХОЗЯЕВА':'ГОСТИ')+(min==null?' (своё время)':(' ('+(''+min).replace('.',',')+' мин)'));
el('mPenM').style.display=(min==null)?'block':'none';
el('mPenN').value='';el('mPenM').value='';el('mPenE').textContent='';
el('mPen').className='modal on';setTimeout(function(){el('mPenN').focus();},50);}
function closePen(){el('mPen').className='modal';}
function openRem(team){penTeam=team;el('mRemT').textContent='Снять штраф: '+(team=='h'?'ХОЗЯЕВА':'ГОСТИ');
el('mRemN').value='';el('mRemE').textContent='';el('mRem').className='modal on';setTimeout(function(){el('mRemN').focus();},50);}
function closeRem(){el('mRem').className='modal';}
function submitPen(){var n=readNum(el('mPenN').value);
if(n<0){el('mPenE').textContent='Номер: целое число 1-99';return;}
var min=penMin;
if(min==null){var sec=parseSec(el('mPenM').value);
if(sec<0){el('mPenE').textContent='Время: MM:SS или MMSS (секунды < 60)';return;}
if(sec%60!==0){closePen();cmd(penTeam=='h'?'pen_add_sec_h':'pen_add_sec_a',n*10000+sec);
toast('Штраф добавлен: №'+n+', '+Math.floor(sec/60)+' мин '+pad2(sec%60)+' сек',true);return;}
min=Math.floor(sec/60);}
if(min<1||min>99){el('mPenE').textContent='Время штрафа: 1-99 минут';return;}
closePen();
/* ПУНКТ 10: «+1,5» = 90 секунд. Команда pen_add_* передаёт минуты целым числом
   (n*100+минуты), поэтому дробные шаблоны уходят через pen_add_sec_* в секундах
   (n*10000+секунды) — ровно так же, как «Своё» время с секундами. */
var msec=Math.round(min*60);
if(msec%60!==0){cmd(penTeam=='h'?'pen_add_sec_h':'pen_add_sec_a',n*10000+msec);
toast('Штраф добавлен: №'+n+', '+Math.floor(msec/60)+' мин '+pad2(msec%60)+' сек',true);return;}
cmd(penTeam=='h'?'pen_add_h':'pen_add_a',n*100+min);
toast('Штраф добавлен: №'+n+', '+min+' мин',true);}
function submitRem(){var n=readNum(el('mRemN').value);
if(n<0){el('mRemE').textContent='Номер: целое число 1-99';return;}
closeRem();pendRem={team:penTeam,num:n,wait:2};
cmd(penTeam=='h'?'pen_rem_h':'pen_rem_a',n);}
function submitFix(){var sec=parseSec(el('fixIn').value);
if(sec<0){toast('Формат коррекции: MM:SS, MMSS или M:SS',false);return;}
cmd('time_set',sec);el('fixIn').value='';
toast('Коррекция времени отправлена: '+pad2(Math.floor(sec/60))+':'+pad2(sec%60),true);}
function start(){el('login').style.display='none';el('ui').style.display='block';poll();}
function relogin(m){token='';try{localStorage.removeItem('tablo_token');}catch(e){}el('ui').style.display='none';el('login').style.display='block';if(m)toast(m,false);}
function doLogin(){var pv=el('pass').value;if(pv===''){el('lmsg').textContent='Введите пароль';return;}
var x=new XMLHttpRequest();x.open('POST','/login',true);
x.onload=function(){if(x.status==200){var r=JSON.parse(x.responseText);token=r.token;
try{localStorage.setItem('tablo_token',token);}catch(e){}
start();}else{var r2={};try{r2=JSON.parse(x.responseText);}catch(e){}
el('lmsg').textContent=(r2.error==4)?('Слишком много попыток. Подождите '+fmtWait(r2.wait)):'Неверный пароль';}};
x.onerror=function(){el('lmsg').textContent='Нет связи с программой';};
x.send('pass='+encodeURIComponent(pv));}
function enterAs(btnId){return function(e){var ev=e||window.event;
if(ev.key==='Enter'||ev.keyCode===13||ev.which===13){
var a=document.activeElement;
if(a&&a.tagName==='BUTTON'){return;}
if(ev.preventDefault){ev.preventDefault();}
el(btnId).onclick();}};}
el('lg').onclick=doLogin;
el('pass').onkeydown=enterAs('lg');
el('bBack').onclick=function(){location.href='/operator';};
var MODE='';
el('bBrkStop').onclick=function(){if(MODE!='BRK'){toast('Перерыв сейчас не идёт',false);return;}cmd('stop',0);toast('Перерыв остановлен',true);};
document.addEventListener('keydown',function(e){var ev=e||window.event;
if(ev.keyCode==32||ev.key===' '){var a=document.activeElement;
if(a&&(a.tagName==='BUTTON'||a.tagName==='INPUT'||a.tagName==='TEXTAREA'))return;
if(ev.preventDefault){ev.preventDefault();}
el('ssBtn').onclick();}});
el('bBreak').onclick=function(){cmd('break',0);};
el('bSiren').onclick=function(){cmd('whistle',0);};
el('bReset').onclick=function(){el('mReset').className='modal on';};
el('mResetC').onclick=function(){el('mReset').className='modal';};
el('mResetOk').onclick=function(){el('mReset').className='modal';cmd('reset',0);toast('Полный сброс матча выполнен',true);};
el('hGp').onclick=function(){cmd('score_h_plus',0);};el('hGm').onclick=function(){cmd('score_h_minus',0);};
el('aGp').onclick=function(){cmd('score_a_plus',0);};el('aGm').onclick=function(){cmd('score_a_minus',0);};
el('hTp').onclick=function(){cmd('timeout_h',0);};el('hTm').onclick=function(){cmd('to_h_dec',0);};
el('aTp').onclick=function(){cmd('timeout_a',0);};el('aTm').onclick=function(){cmd('to_a_dec',0);};
/* ПУНКТ 10: шаблон «+1,5» = 90 секунд. Целые минуты уходят командой pen_add_*,
   а 1,5 мин - pen_add_sec_* (секунды), см. submitPen(). */
el('hP15').onclick=function(){openPen('h',1.5);};
el('hP2').onclick=function(){openPen('h',2);};el('hP4').onclick=function(){openPen('h',4);};el('hP5').onclick=function(){openPen('h',5);};el('hPc').onclick=function(){openPen('h',null);};
el('aP15').onclick=function(){openPen('a',1.5);};
el('aP2').onclick=function(){openPen('a',2);};el('aP4').onclick=function(){openPen('a',4);};el('aP5').onclick=function(){openPen('a',5);};el('aPc').onclick=function(){openPen('a',null);};
el('hRem').onclick=function(){openRem('h');};el('aRem').onclick=function(){openRem('a');};
el('pPrev').onclick=function(){cmd('period_prev',0);};el('pNext').onclick=function(){cmd('period_next',0);};
el('ssBtn').onclick=function(){if(MODE=='CLK'||MODE=='PRE'){cmd('splash_hide',0);return;}cmd(RUN?'stop':'start',0);};
el('fixOk').onclick=submitFix;
el('fixIn').onkeydown=enterAs('fixOk');
el('mPenC').onclick=closePen;
el('mPenOk').onclick=submitPen;
el('mPenN').onkeydown=enterAs('mPenOk');
el('mPenM').onkeydown=enterAs('mPenOk');
el('mRemC').onclick=closeRem;
el('mRemOk').onclick=submitRem;
el('mRemN').onkeydown=enterAs('mRemOk');
function fmtP(v){v=+v||0;if(v<0)v=0;return pad2(Math.floor(v/60))+':'+pad2(v%60);}
function cmdText(k,v){if(!token)return;var x=new XMLHttpRequest();
x.open('GET','/cmdtext?key='+k+'&v='+encodeURIComponent(v),true);setTok(x);
x.onload=function(){if(x.status==403){relogin('Сессия завершена — войдите снова');}};
x.send();}
el('rm').oninput=function(){refDirty=true;};
el('ro').oninput=function(){refDirty=true;};
el('bRefRemember').onclick=function(){refDirty=false;cmd('ref_remember',0);toast('Запоминание эталона запрошено',true);};
el('bRefEdit').onclick=function(){var a=parseSec(el('rm').value),b=parseSec(el('ro').value);
if(a<0||b<0){toast('Эталон: формат MM:SS или MMSS в обоих полях',false);return;}
refDirty=false;cmdText('ref_edit',a+'|'+b);toast('Правка эталона отправлена',true);};
(function(){var t='';try{t=localStorage.getItem('tablo_token')||'';}catch(e){}
if(!t)return;token=t;
var x=new XMLHttpRequest();x.open('GET','/sessioncheck',true);try{x.setRequestHeader('X-Tablo-Token',t);}catch(e){}
x.onload=function(){if(x.status==200){start();}else{token='';try{localStorage.removeItem('tablo_token');}catch(e){}}};
x.onerror=function(){start();};
x.send();})();
</script>
</body>
</html>";
	}
		private static string OperatorHtml()
	{
		return @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Tablo operator</title>
<style>
html,body{margin:0;background:#1b1b1b;color:#eee;font-family:Consolas,monospace;}
button{font-size:14px;padding:8px 10px;margin:2px;background:#333;color:#eee;border:1px solid #666;cursor:pointer;}
input{font-size:14px;padding:6px;margin:2px;background:#222;color:#eee;border:1px solid #666;width:86px;}
#topbar{position:sticky;top:0;background:#101010;display:flex;flex-wrap:wrap;align-items:stretch;padding:6px 10px;border-bottom:1px solid #333;z-index:5;}
#topbar span{flex:1;font-size:16px;align-self:center;color:#fff;}
/* ===== ПУНКТ 7: три кнопки в правом верхнем углу ОДИНАКОВОГО размера =====
   Порядок: «Мобильный вид», «Пульт оператора», «Выйти».
   align-self:stretch + одинаковая ширина/паддинги => равны и ширина, и высота. */
#topbar button{width:150px;font-size:13px;padding:8px 4px;margin:0 0 0 8px;box-sizing:border-box;align-self:stretch;white-space:normal;}
/* ===== ПУНКТ 1: форма входа — текст читаем при любой теме и любом разрешении =====
   Фон и цвет задаются ЯВНО и парой (чёрный фон / белый текст — максимальный контраст),
   поэтому ни светлая, ни тёмная тема браузера, ни автозаполнение пароля,
   ни контрастная тема Windows не делают текст невидимым. */
#login{margin:8vh auto;width:320px;max-width:94vw;text-align:center;color:#fff;}
#login h3{color:#fff;font-size:20px;margin:0 0 10px;}
#login #pass{width:100%;box-sizing:border-box;background:#000;color:#fff;-webkit-text-fill-color:#fff;border:2px solid #9aa0a6;border-radius:6px;font-size:22px;padding:12px;margin:2px 0;}
#login #pass:focus{border-color:#ffd54f;outline:2px solid #ffd54f;}
/* placeholder — явно и одинаково во всех движках (контраст 7:1 на чёрном) */
#login #pass::-webkit-input-placeholder{color:#bdbdbd;opacity:1;}
#login #pass:-ms-input-placeholder{color:#bdbdbd;}
#login #pass::-moz-placeholder{color:#bdbdbd;opacity:1;}
#login #pass::placeholder{color:#bdbdbd;opacity:1;}
/* автозаполнение браузера красит поле в светлое — принудительно возвращаем чёрный фон
   и белый текст, иначе введённый пароль не виден */
#login #pass:-webkit-autofill,#login #pass:-webkit-autofill:hover,#login #pass:-webkit-autofill:focus{-webkit-text-fill-color:#fff;box-shadow:inset 0 0 0 1000px #000;border:2px solid #9aa0a6;}
/* контрастная/принудительная тема Windows: не даём перекрасить только одну половину пары */
#login #pass,#login button,#login #msg{forced-color-adjust:none;}
#login #rem{width:auto;min-width:0;min-height:0;padding:0;margin:0 6px 0 0;background:transparent;border:0;vertical-align:middle;}
#login label{display:block;margin:8px 0;color:#f0f0f0;font-size:15px;text-align:left;}
#login button{width:100%;box-sizing:border-box;font-size:18px;padding:12px;margin:6px 0;background:#2d6a2d;color:#fff;border:1px solid #8fd18f;border-radius:6px;}
#login #viewToggleLogin{font-size:14px;padding:8px;background:#333;color:#fff;border:1px solid #777;}
#msg{color:#ffb3b3;min-height:20px;font-size:15px;}
#login #msg{background:#33161a;border:1px solid #8a3b45;border-radius:6px;padding:6px 8px;margin:6px 0;}
#login #msg:empty{background:transparent;border-color:transparent;padding:0;margin:0;}
#panel{display:none;}
#grid{column-width:270px;column-gap:10px;padding:10px;}
details{border:1px solid #444;background:#202020;margin:0 0 10px 0;break-inside:avoid;display:block;}
summary{cursor:pointer;font-weight:bold;padding:8px 10px;background:#2a2a2a;}
.card{padding:8px;display:flex;flex-direction:column;}
.card>*{margin:3px 0;}
.row{display:flex;flex-wrap:wrap;}
.row button{flex:1 1 45%;min-width:80px;}
.team{flex:1;}
.team.on{background:#2a5a2a;border-color:#6c6;color:#fff;}
#refShow{font-size:12px;color:#9c9;}
/* ПУНКТ 6: счётчики тайм-аутов под кнопками — видно результат «удал.» */
.cntLine{font-size:13px;color:#9c9;text-align:center;margin:2px 0;}
.sp{display:grid;grid-template-columns:repeat(3,1fr);grid-gap:4px;gap:4px;}
.sp button{font-size:12px;padding:6px 2px;margin:0;}
.sp button.on{background:#2a5a2a;border-color:#6c6;color:#fff;}
#godBtn.on{background:#a22;border-color:#f66;color:#fff;}
#adminList div{border:1px solid #333;padding:6px;margin:4px 0;font-size:12px;}
#adminList button{font-size:12px;padding:4px 8px;margin-left:4px;width:auto;}
body.mob button{font-size:18px;padding:12px 8px;min-height:44px;}
body.mob input{font-size:18px;padding:10px;width:100px;}
body.mob summary{font-size:18px;padding:12px;}
/* ПУНКТ 7 (мобильный вид): заголовок на своей строке, три кнопки делят ширину поровну
   (flex:1 1 0 => одинаковая ширина при любом экране), align-self:stretch => одинаковая высота) */
body.mob #topbar{padding:6px;}
body.mob #topbar span{flex:1 1 100%;font-size:16px;margin:0 0 6px;}
body.mob #topbar button{flex:1 1 0;width:auto;min-width:0;font-size:13px;padding:10px 2px;margin:0 0 0 6px;align-self:stretch;}
body.mob .row button{min-width:120px;}
body.mob .sp button{font-size:14px;padding:10px 2px;}
@media (max-width:700px){button{min-height:44px;}input{min-height:38px;}}
/* БЛОК 6, З1/З2/З7: цвета Старт/Стоп, тост, модальные окна подтверждения, список удалений */
#bStart,#bStop{color:#fff;}
#opToast{position:fixed;left:50%;bottom:18px;margin-left:-45vw;width:90vw;max-width:520px;background:#222;border:1px solid #555;border-radius:6px;padding:10px 18px;font-size:15px;display:none;z-index:60;text-align:center;}
#opToast.ok{border-color:#4c4;color:#cfc;}
#opToast.err{border-color:#c44;color:#fcc;}
.modalO{position:fixed;left:0;top:0;right:0;bottom:0;background:rgba(0,0,0,0.65);display:none;z-index:50;}
.modalO.on{display:block;}
.boxO{background:#1b1b1b;border:1px solid #555;border-radius:8px;padding:16px;width:330px;max-width:92vw;margin:15vh auto 0 auto;}
.boxO h3{margin:0 0 10px;font-size:18px;}
.boxO .errO{color:#f88;font-size:13px;min-height:18px;margin:6px 0;}
.boxO input.txtO{background:#222;color:#eee;border:1px solid #666;border-radius:6px;font-size:16px;padding:8px;width:100%;box-sizing:border-box;}
.actsO{margin-top:10px;}
.actsO button{width:48%;font-size:15px;padding:10px 4px;}
.actsO button.warnO{background:#7a2020;border-color:#c66;color:#fff;}
.penList{border:1px solid #444;background:#181818;min-height:36px;margin:4px 0;}
.penList div{padding:6px 6px;border-bottom:1px dashed #333;cursor:pointer;font-size:14px;}
.penList div.sel{background:#2a5a2a;color:#fff;}
.penList div.empty{color:#888;cursor:default;}
/* ПУНКТ 3: блок счётчика неудачных попыток входа */
#failBox{border:1px solid #444;background:#181818;padding:8px;margin:0 0 10px;}
#failBox .failHead{font-weight:bold;font-size:14px;margin-bottom:4px;color:#fff;}
#failInfo{font-size:13px;color:#ddd;line-height:1.4;margin-bottom:6px;}
#bFailReset{width:auto;font-size:13px;padding:8px 12px;margin:0;}
body.mob .penList div{padding:10px 6px;font-size:17px;}
/* ПУНКТ 12: архив времени */
.archList{border:1px solid #444;background:#181818;min-height:36px;margin:4px 0;}
.archList div.empty{padding:6px;color:#999;font-size:13px;}
.archList .arow{padding:6px;border-bottom:1px dashed #333;cursor:pointer;}
.archList .arow.sel{background:#2a5a2a;}
.archList .ar1{font-size:14px;color:#fff;}
.archList .ar2{font-size:12px;color:#9c9;margin-top:2px;}
.archList .arow.sel .ar1,.archList .arow.sel .ar2{color:#fff;}
body.mob .archList .arow{padding:9px 6px;}
body.mob .archList .ar1{font-size:17px;}
body.mob .archList .ar2{font-size:14px;}
/* ПУНКТ 4: окно снятия — сетка 2×2 (клетки табло) */
.remGrid{display:grid;grid-template-columns:1fr 1fr;grid-gap:6px;gap:6px;margin:6px 0;}
.remCell{background:#222;border:2px solid #555;color:#eee;padding:8px 4px;margin:0;min-height:58px;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;cursor:pointer;}
.remCell .remCap{font-size:11px;color:#9a9a9a;display:block;}
.remCell .remVal{font-size:17px;color:#fff;display:block;margin-top:3px;}
.remCell.busy{border-color:#6c6;}
.remCell.sel{background:#2a5a2a;border-color:#8f8;}
.remCell.sel .remCap,.remCell.sel .remVal{color:#fff;}
.remCell.emptyCell{border-color:#3a3a3a;cursor:default;}
.remCell.emptyCell .remVal{color:#777;}
.remHint{font-size:12px;color:#bbb;line-height:1.35;margin:2px 0 6px;text-align:left;}
.remOr{font-size:12px;color:#999;text-align:center;margin:8px 0 4px;}
#mRemO .team{flex:1 1 45%;min-width:80px;}
#mRemO .team.on{background:#2a5a2a;border-color:#6c6;color:#fff;}
body.mob .remCell{min-height:64px;}
body.mob .remCell .remVal{font-size:20px;}
</style>
</head>
<body>
<div id='login'>
<h3>Панель оператора</h3>
<div id='msg'></div>
<input type='password' id='pass' placeholder='Пароль'>
<label><input type='checkbox' id='rem'>Запомнить вход</label>
<button data-k='__login' type='button'>Войти</button>
<button id='viewToggleLogin' type='button'>Вид</button>
</div>
<div id='panel'>
<div id='topbar'><span>Панель оператора</span><button id='viewToggle'>Мобильный вид</button><button id='goPanel' type='button'>Пульт оператора</button><button data-k='__logout' type='button'>Выйти</button></div>
<div id='grid'>
<details open><summary>Игра</summary><div class='card'>
<div class='row'><button id='bStart' data-k='start'>СТАРТ</button><button id='bStop' data-k='stop'>СТОП</button><button data-k='whistle'>СВИСТОК</button></div>
<div class='row'><button data-k='break'>ПЕРЕРЫВ</button><button data-k='timeout_h'>ТАЙМАУТ ХОЗ</button><button data-k='timeout_a'>ТАЙМАУТ ГОСТ</button></div>
<!-- ПУНКТ 6: «удал.» = убрать (уменьшить) счётчик тайм-аутов команды.
     Команды to_h_dec / to_a_dec на сервере уже были — кнопок в панели не было.
     Отдельная строка, размер кнопок тот же, что у «ТАЙМАУТ ХОЗ/ГОСТ» (класс .row). -->
<div class='row'><button data-k='to_h_dec' type='button'>Таймаут хоз. удал.</button><button data-k='to_a_dec' type='button'>Таймаут гост. удал.</button></div>
<div id='toCount' class='cntLine'>Таймаутов: хоз 0 | гос 0</div>
<div class='row'><button data-k='reset'>МАСТЕР-СБРОС</button></div>
</div></details>
<details><summary>Счёт и период</summary><div class='card'>
<div class='row'><button data-k='score_h_plus'>ХОЗ+1</button><button data-k='score_h_minus'>ХОЗ-1</button></div>
<div class='row'><button data-k='score_a_plus'>ГОС+1</button><button data-k='score_a_minus'>ГОС-1</button></div>
<div class='row'><button data-k='period_next'>ПЕРИОД+1</button><button data-k='period_prev'>ПЕРИОД-1</button></div>
</div></details>
<details open><summary>Удаления</summary><div class='card'>
<div class='row'><input id='pn' value='4' placeholder='№ 1-99' title='Номер удалённого игрока (1-99)' inputmode='numeric' pattern='[0-9]*'><button id='teamH' class='team on'>ХОЗЯЕВА</button><button id='teamA' class='team'>ГОСТИ</button></div>
<div class='sp'><button data-k='pen_q' data-m='1.5'>1,5 мин</button><button data-k='pen_q' data-m='2'>2 мин</button><button data-k='pen_q' data-m='4'>4 мин</button><button data-k='pen_q' data-m='5'>5 мин</button><button data-k='pen_q' data-m='10'>10 мин</button></div>
<div id='penList' class='penList'><div class='empty'>нет удалений</div></div>
<div class='row'><button id='bPenRemSel' type='button'>СНЯТЬ</button></div>
</div></details>
<details><summary>Время матча</summary><div class='card'>
<div class='row'><input id='t' value='13:27' inputmode='numeric' pattern='[0-9]*'><button data-k='time_set'>СОХРАНИТЬ ВРЕМЯ</button></div>
<div class='row'><input id='gm' placeholder='MM:SS или MMSS' inputmode='numeric' pattern='[0-9]*'><button data-k='cfg_main'>ВРЕМЯ ПЕРИОДА</button></div>
<div class='row'><input id='ot' placeholder='MM:SS или MMSS' inputmode='numeric' pattern='[0-9]*'><button data-k='cfg_ot'>ОВЕРТАЙМ</button></div>
<div class='row'><input id='brk' placeholder='MM:SS или MMSS' inputmode='numeric' pattern='[0-9]*'><button data-k='break_set'>ВРЕМЯ ПЕРЕРЫВА</button></div>
<div class='row'><input id='ms' placeholder='HH:MM или HHMM' inputmode='numeric' pattern='[0-9]*'><button data-k='match_start_set'>НАЧ. МАТЧА</button><button data-k='prestart_arm' type='button'>Запуск заставки и таймера</button></div>
<div class='row'><button data-k='splash_hide' type='button'>Остановить заставку и таймер</button></div>
<div id='refShow'>Эталон: -</div>
<div class='row'><input id='rm' placeholder='MM:SS или MMSS' inputmode='numeric' pattern='[0-9]*'><input id='ro' placeholder='MM:SS или MMSS' inputmode='numeric' pattern='[0-9]*'><button data-k='ref_remember'>ЗАПОМНИТЬ</button><button data-k='ref_edit'>ПРАВИТЬ</button></div>
</div></details>
<!-- ПУНКТ 12: архив времени - 6 последних «стопов» (аналог десктопного «Архив времени»).
     Для каждого стопа: ВРЕМЯ МАТЧА, ВРЕМЯ АКТИВНЫХ ПОЗИЦИЙ, СЧЁТ, ПЕРИОД. -->
<details><summary>Архив времени</summary><div class='card'>
<div class='cntLine'>6 последних остановок: время матча, активные позиции, счёт, период</div>
<div id='archList' class='archList'><div class='empty'>нет записей</div></div>
<div class='row'><button id='bArchApply' type='button'>Применить выбранное</button></div>
</div></details>
<details><summary>Названия и города</summary><div class='card'>
<div class='row'><input id='nh' placeholder='имя хоз'><input id='th' placeholder='город хоз'></div>
<div class='row'><input id='na' placeholder='имя гос'><input id='ta' placeholder='город гос'></div>
<div class='row'><button data-k='names_set'>ПРИМЕНИТЬ НАЗВАНИЯ</button></div>
</div></details>
<details><summary>Прочие</summary><div class='card'>
<div class='row'><button id='godBtn' data-k='god_toggle'>Режим бога</button></div>
<div class='sp'><button data-k='speed_set' data-v='80'>0.80</button><button data-k='speed_set' data-v='90'>0.90</button><button data-k='speed_set' data-v='100'>1</button><button data-k='speed_set' data-v='110'>1.1</button><button data-k='speed_set' data-v='125'>1.25</button><button data-k='speed_set' data-v='130'>1.3</button><button data-k='speed_set' data-v='150'>1.5</button><button data-k='speed_set' data-v='175'>1.75</button><button data-k='speed_set' data-v='200'>2</button></div>
</div></details>
<details id='secAdmin' style='display:none;'><summary>Администрирование (супер)</summary><div class='card'>
<!-- ПУНКТ 3: счётчик неудачных попыток входа и его ручной сброс (только супер) -->
<div id='failBox'>
<div class='failHead'>Счётчик неудачных попыток входа</div>
<div id='failInfo'>нет данных</div>
<button id='bFailReset' type='button'>Сбросить счётчик</button>
</div>
<div id='adminList'></div>
</div></details>
</div>
</div>
<div id='opToast'></div>
<div class='modalO' id='mResetO'><div class='boxO'>
<h3>ВНИМАНИЕ: полный сброс матча</h3>
<div style='font-size:14px;line-height:1.5;'>Обнулятся счёт, период, игровое время, все удаления и таймауты обеих команд. Нажмите СБРОС для подтверждения или Отмена для выхода.</div>
<div class='actsO'><button id='mResetOC'>Отмена</button><button id='mResetOOk' class='warnO'>СБРОС</button></div>
</div></div>
<div class='modalO' id='mFailO'><div class='boxO'>
<h3>Сброс счётчика попыток входа</h3>
<div id='failPrompt' style='white-space:pre-line;font-size:14px;line-height:1.5;'></div>
<input class='txtO' id='mFailV' placeholder='Повторите значение' inputmode='numeric' pattern='[0-9]*'>
<div class='errO' id='mFailE'></div>
<div class='actsO'><button id='mFailC' type='button'>Отмена</button><button id='mFailOk' type='button'>СБРОСИТЬ</button></div>
</div></div>
<div class='modalO' id='mArchO'><div class='boxO'>
<h3>Применить снимок архива</h3>
<div id='archPrompt' style='white-space:pre-line;font-size:14px;line-height:1.5;'></div>
<div class='actsO'><button id='mArchC' type='button'>Отмена</button><button id='mArchOk' type='button' class='warnO'>ПРИМЕНИТЬ</button></div>
</div></div>
<div class='modalO' id='mRemO'><div class='boxO'>
<h3 id='mRemOT'>Снять удаление</h3>
<!-- ПУНКТ 4: окно снятия по схеме 2x2 - ровно как клетки табло:
     1x1 хозяева | 1x2 гости
     2x1 хозяева | 2x2 гости
     Гости начинаются со второй (правой) половины. -->
<div class='remGrid'>
<button type='button' class='remCell' id='rc_h0'><span class='remCap'>1×1 ХОЗЯЕВА</span><span class='remVal'>—</span></button>
<button type='button' class='remCell' id='rc_a0'><span class='remCap'>1×2 ГОСТИ</span><span class='remVal'>—</span></button>
<button type='button' class='remCell' id='rc_h1'><span class='remCap'>2×1 ХОЗЯЕВА</span><span class='remVal'>—</span></button>
<button type='button' class='remCell' id='rc_a1'><span class='remCap'>2×2 ГОСТИ</span><span class='remVal'>—</span></button>
</div>
<div class='remHint'>Нажмите клетку со строкой, которую нужно снять, затем «СНЯТЬ». Пустая клетка недоступна.</div>
<div class='remOr'>— или по номеру игрока —</div>
<div class='row'><button id='mRemTH' type='button' class='team on'>ХОЗЯЕВА</button><button id='mRemTA' type='button' class='team'>ГОСТИ</button></div>
<input class='txtO' id='mRemON' placeholder='Номер игрока 1-99' inputmode='numeric' pattern='[0-9]*'>
<div class='errO' id='mRemOE'></div>
<div class='actsO'><button id='mRemOC' type='button'>Отмена</button><button id='mRemOOk' type='button'>СНЯТЬ</button></div>
</div></div>
<script>
var token='';var penTeam='h';var isSuper=false;var loginBusy=false;
/* БЛОК 6: совместимость со старыми браузерами (IE8 на XP): Date.now и addEventListener */
if(!Date.now){Date.now=function(){return new Date().getTime();};}
function addEv(o,t,f){if(o.addEventListener){o.addEventListener(t,f,false);}else if(o.attachEvent){o.attachEvent('on'+t,function(){return f(window.event);});}}
function setTxt(e,t){if(!e)return;if(typeof e.textContent!=='undefined'&&('textContent' in e)){e.textContent=t;}else{e.innerText=t;}}
function toastO(m,ok){var t=el('opToast');if(!t)return;setTxt(t,m);t.className=ok?'ok':'err';t.style.display='block';if(t._tm){clearTimeout(t._tm);}t._tm=setTimeout(function(){t.style.display='none';},3500);}
function pad2O(n){return (n<10?'0':'')+n;}
var RUN=false;var ELAPS=0;var penSel=null;var penListSig='';var pendRemO=null;
function setTok(x){try{x.setRequestHeader('X-Tablo-Token',token);}catch(e){}}
function fmtWait(s){s=+s||60;if(s>=3600)return Math.round(s/3600)+' ч';if(s>=60)return Math.round(s/60)+' мин';return s+' сек';}
function el(id){return document.getElementById(id);}
function show(m){el('msg').textContent=m||'';if(el('panel').style.display=='block'&&m){toastO(m,false);}}
function esc(s){return (''+(s||'')).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');}
function showPanel(){el('login').style.display='none';el('panel').style.display='block';
if(isSuper){var a=el('secAdmin');if(a)a.style.display='';adminPoll();}
pollNow();}
function logoutUI(m){token='';isSuper=false;loginBusy=false;try{localStorage.removeItem('tablo_token');}catch(e){}
el('panel').style.display='none';el('login').style.display='block';show(m||'');}
function isMobileUA(){return /Android|iPhone|iPad|iPod|Mobile|Silk|Opera Mini|IEMobile/i.test(navigator.userAgent||'');}
function currentMob(){var p='';try{p=localStorage.getItem('tablo_view')||'';}catch(e){}
if(p=='m')return true;if(p=='d')return false;return isMobileUA();}
function applyView(){document.body.className=currentMob()?'mob':'';
var L=currentMob()?'Вид с ПК':'Мобильный вид';
var a=el('viewToggle');if(a)a.textContent=L;var b=el('viewToggleLogin');if(b)b.textContent=L;}
function toggleView(){try{localStorage.setItem('tablo_view',currentMob()?'d':'m');}catch(e){}applyView();}
function boot(){applyView();var t='';try{t=localStorage.getItem('tablo_token')||'';}catch(e){}
if(!t)return;token=t;
var x=new XMLHttpRequest();x.open('GET','/sessioncheck',true);try{x.setRequestHeader('X-Tablo-Token',t);}catch(e){}
x.onload=function(){if(x.status==200){var r={};try{r=JSON.parse(x.responseText);}catch(e){}
isSuper=(r.super==1);showPanel();}else{logoutUI('');}};
x.onerror=function(){showPanel();};x.send();}
function doLogin(){
if(loginBusy)return;
var pv=el('pass').value;
if(pv===''){show('Введите пароль, затем Enter или кнопку Войти.');return;}
loginBusy=true;show('');
var rem=el('rem').checked?1:0;
var x=new XMLHttpRequest();x.open('POST','/login',true);
x.onload=function(){loginBusy=false;
if(x.status==200){var r=JSON.parse(x.responseText);token=r.token;
if(rem){try{localStorage.setItem('tablo_token',token);}catch(e){}}else{try{localStorage.removeItem('tablo_token');}catch(e){}}
var y=new XMLHttpRequest();y.open('GET','/sessioncheck',true);try{y.setRequestHeader('X-Tablo-Token',token);}catch(e){}
y.onload=function(){if(y.status==200){var q={};try{q=JSON.parse(y.responseText);}catch(e){}isSuper=(q.super==1);}showPanel();};
y.onerror=function(){showPanel();};y.send();}
else{var r={};try{r=JSON.parse(x.responseText);}catch(e){}
if(r.error==2){show('Уже подключены 2 устройства. Завершите сессию кнопкой Выйти или войди супер-паролем.');}
else if(r.error==3){show('Слишком большой запрос.');}
else if(r.error==4){show('Сработала пауза после неудачных попыток входа: ждать '+fmtWait(r.wait)+' (уровень '+(r.level||1)+', неудач подряд: '+(r.series||0)+'). Вход с повышенными правами проходит без паузы.');}
else if(r.error==5){show('Устройство забанено на 30 минут.');}
else{show('Неверный пароль');}}};
x.onerror=function(){loginBusy=false;show('Нет связи с сервером. Повторите попытку.');};
x.send('pass='+encodeURIComponent(pv));}
function loginEnter(e){var ev=e||window.event;
if(!(ev.key==='Enter'||ev.keyCode===13||ev.which===13))return;
var l=el('login');if(!l||l.style.display==='none')return;
var a=document.activeElement;
if(a&&a.getAttribute&&a.getAttribute('data-k')==='__login')return;
if(ev.preventDefault){ev.preventDefault();}
doLogin();}
addEv(document,'keydown',loginEnter);
/* ===== ПУНКТ 8: КОМПАКТНЫЙ ВВОД ВРЕМЕНИ =====
   Разбор принимает время и С разделителем, и БЕЗ него:
     15:55 / 1555 -> 15 мин 55 сек;  155 -> 1:55;  20 -> 20 мин;  012005 -> 1:20:05.
   Разделителями считаются ':' , '.' , '-' , ',' , '/' и пробел - все приводятся к ':'.
   Работает одинаково во всех полях времени панели оператора: время матча, период,
   овертайм, перерыв, начало матча, эталон. */
function mmss(s){var v=(''+(s==null?'':s)).replace(/^\s+|\s+$/g,'');if(v==='')return -1;
v=v.replace(/[.,\-\/\s]/g,':').replace(/:{2,}/g,':').replace(/^:+|:+$/g,'');
if(v==='')return -1;
var parts=v.split(':');var h=0,m=0,ss=0;
if(parts.length===3){h=+parts[0];m=+parts[1];ss=+parts[2];}
else if(parts.length===2){m=+parts[0];ss=+parts[1];}
else if(parts.length===1){var d=parts[0];if(!/^\d+$/.test(d))return -1;
if(d.length===4){m=+d.substr(0,2);ss=+d.substr(2,2);}
else if(d.length===3){m=+d.substr(0,1);ss=+d.substr(1,2);}
else if(d.length===6){h=+d.substr(0,2);m=+d.substr(2,2);ss=+d.substr(4,2);}
else if(d.length<=2){m=+d;}
else return -1;}
else return -1;
if(isNaN(h)||isNaN(m)||isNaN(ss))return -1;
if(ss<0||ss>59||m<0||m>59||h<0||h>9)return -1;
return h*3600+m*60+ss;}
/* ПУНКТ 8/11: чтение поля времени. При ошибке формат НЕ уходит на сервер молча
   (раньше отправлялось -1 и программа просто игнорировала команду) - оператор
   сразу видит причину в тосте. */
function timeOrWarn(id,label){var e=el(id);var v=mmss(e?e.value:'');
if(v<0){toastO(label+': формат MM:SS или MMSS (например 1555 = 15:55)',false);
if(e){try{e.focus();}catch(err){}}return -1;}
return v;}
function fmts(v){v=+v;if(isNaN(v)||v<0)return '';var m=Math.floor(v/60),ss=v%60;return (m<10?'0':'')+m+':'+(ss<10?'0':'')+ss;}
function fref(v){v=+v;if(isNaN(v)||v<0)return '—';return fmts(v);}
function vsec(v){if(!v)return -1;var p=(''+v).split(':');return (+p[0])*60+(+(p[1]||0));}
function setIf(id,val){var e=el(id);if(e&&document.activeElement!==e){e.value=val;}}
function uenc(s){var r='';for(var i=0;i<s.length;i++){var ch=s.charAt(i),c=s.charCodeAt(i);
if(ch=='|'||ch=='\\'){r+='\\'+ch;continue;}
if(c<32||c>126){r+='\\u'+('000'+c.toString(16)).slice(-4);}else{r+=ch;}}return r;}
function sendCmd(k,p){if(!token)return;var x=new XMLHttpRequest();
x.open('GET','/cmd?key='+k+'&p='+p,true);setTok(x);
x.onload=function(){if(x.status==403){logoutUI('Сессия завершена.');}else{setTimeout(pollNow,400);}};
x.onerror=function(){setTimeout(pollNow,1500);};
x.send();}
function cmdText(k,v){if(!token)return;var x=new XMLHttpRequest();
x.open('GET','/cmdtext?key='+k+'&v='+encodeURIComponent(v),true);setTok(x);
x.onload=function(){if(x.status==403){logoutUI('Сессия завершена.');}else{setTimeout(pollNow,400);}};
x.onerror=function(){setTimeout(pollNow,1500);};
x.send();}
function cmd(k){if(!token)return;
/* ПУНКТ 11: ввод/корректировка времени периода больше НИЧЕМ не заблокирован -
   ни чекбоксом, ни режимом (вид с ПК / мобильный вид), ни фактом идущего матча.
   ПУНКТ 8: значение принимается и как MM:SS, и как MMSS. */
if(k=='time_set'){var vT=timeOrWarn('t','Время матча');if(vT<0)return;
sendCmd(k,vT);toastO('Время периода выставлено: '+fmts(vT),true);return;}
if(k=='cfg_main'){var vG=timeOrWarn('gm','Время периода');if(vG<0)return;
sendCmd(k,vG);toastO('Время периода сохранено: '+fmts(vG),true);return;}
if(k=='cfg_ot'){var vO=timeOrWarn('ot','Овертайм');if(vO<0)return;
sendCmd(k,vO);toastO('Овертайм сохранён: '+fmts(vO),true);return;}
if(k=='break_set'){var vB=timeOrWarn('brk','Время перерыва');if(vB<0)return;
sendCmd(k,vB);toastO('Время перерыва сохранено: '+fmts(vB),true);return;}
if(k=='match_start_set'){var vM=timeOrWarn('ms','Начало матча');if(vM<0)return;
sendCmd(k,vM);toastO('Начало матча сохранено',true);return;}
if(k=='prestart_arm'){var vP=timeOrWarn('ms','Начало матча');if(vP<0)return;
sendCmd(k,vP);toastO('Запуск заставки и таймера отправлен',true);return;}
if(k=='ref_remember'){refDirty=false;sendCmd(k,0);return;}
if(k=='ref_edit'){var ra=timeOrWarn('rm','Эталон осн.'),rb=timeOrWarn('ro','Эталон овт.');
if(ra<0||rb<0)return;
refDirty=false;cmdText('ref_edit',ra+'|'+rb);toastO('Эталон отправлен: '+fmts(ra)+' / '+fmts(rb),true);return;}
sendCmd(k,0);}
function doLogout(){var x=new XMLHttpRequest();x.open('GET','/logout',true);setTok(x);
x.onload=function(){logoutUI('Вы вышли из системы.');};x.send();}
function applyState(s){try{
RUN=(s.run==1);ELAPS=+(s.elapsed||0);updSS();renderPenList(s);tickPendRem(s);renderArch(s);
setIf('gm',fmts(s.game_sec));setIf('ot',fmts(s.ot_sec));setIf('brk',fmts(s.break_sec));
setIf('ms',s.match_start||'');
setIf('nh',s.name_h||'');setIf('th',s.town_h||'');setIf('na',s.name_a||'');setIf('ta',s.town_a||'');
var remembered=((+s.ref_main)>=0)||((+s.ref_ot)>=0);
var key=(s.ref_main)+'|'+(s.ref_ot)+'|'+(s.ref_lock);
if(refKey===''){refKey=key;}
var changed=(key!==refKey);
if(changed){refKey=key;refDirty=false;}
var rs=el('refShow');
if(rs){
if(remembered){rs.textContent='Эталон: осн '+fmts(s.ref_main)+' | овт '+fmts(s.ref_ot)+' | '+((s.ref_lock==1)?'БЛОК':'ОТКР');}
else{rs.textContent='Эталон: осн '+fmts(s.game_sec)+' | овт '+fmts(s.ot_sec)+' | НЕ ЗАПОМНЕН (текущая настройка)';}
}
if(!refDirty&&(changed||!remembered)){
var eR=el('rm'),eO=el('ro');
if(eR&&document.activeElement!==eR){eR.value=remembered?fmts(s.ref_main):fmts(s.game_sec);}
if(eO&&document.activeElement!==eO){eO.value=remembered?fmts(s.ref_ot):fmts(s.ot_sec);}
}
/* ПУНКТ 6: счётчики тайм-аутов команд */
var tc=el('toCount');if(tc){setTxt(tc,'Таймаутов: хоз '+((+s.to_h)||0)+' | гос '+((+s.to_a)||0));}
var g=el('godBtn');if(g){if(s.god==1){g.className='on';g.textContent='РЕЖИМ БОГА: ВКЛ';}else{g.className='';g.textContent='Режим бога';}}
var sp=document.querySelectorAll('[data-k=""speed_set""]');
for(var i=0;i<sp.length;i++){var cur=Math.round(parseFloat(s.speed||'1')*100);sp[i].className=(''+cur==sp[i].getAttribute('data-v'))?'on':'';}
}catch(e){}}
var pollMs=7000;var pollTimer=null;
function pollNow(){if(pollTimer){clearTimeout(pollTimer);pollTimer=null;}doPoll();}
function doPoll(){if(!token){return;}
if(el('panel').style.display!='block'){pollTimer=setTimeout(doPoll,3000);return;}
var x=new XMLHttpRequest();x.open('GET','/state.json?ts='+Date.now(),true);setTok(x);
x.onload=function(){if(x.status==403){logoutUI('Сессия завершена.');return;}pollTimer=setTimeout(doPoll,pollMs);
if(x.status!=200){return;}
var raw=x.responseText;var s=null;try{s=JSON.parse(raw);}catch(e){s=null;}
if(!s||typeof s!=='object'){if(window.console){console.log('operator: не-JSON, тело: '+raw.substring(0,200));}return;}
if(el('panel').style.display=='block'){applyState(s);}
pollMs=(pendRemO!=null)?1000:(((s.mode=='GAME')&&vsec(s.value)>=0&&vsec(s.value)<=60)?1000:7000);};
x.onerror=function(){pollTimer=setTimeout(doPoll,3000);};
x.send();}
function adminPoll(){if(!isSuper||!token)return;loadFailState();var x=new XMLHttpRequest();
x.open('GET','/admin',true);setTok(x);
x.onload=function(){if(x.status==200){try{renderAdmin(JSON.parse(x.responseText));}catch(e){}}setTimeout(adminPoll,7000);};
x.onerror=function(){setTimeout(adminPoll,7000);};
x.send();}
function renderAdmin(list){var box=el('adminList');if(!box)return;box.innerHTML='';
for(var i=0;i<list.length;i++){var s=list[i];
var d=document.createElement('div');
var ua=(''+(s.ua||'')).substring(0,48);
d.innerHTML=esc(s.ip)+' | '+esc(ua)+' | простой '+s.idle+' мин'+(s.super==1?' | СУПЕР':'')+(s.me==1?' (это вы)':'');
if(s.me!=1){
var bk=document.createElement('button');bk.textContent='КИК';bk.setAttribute('data-k','admin_kick');bk.setAttribute('data-t',s.sid);d.appendChild(bk);
if(s.super!=1){var bb=document.createElement('button');bb.textContent='БАН 30';bb.setAttribute('data-k','admin_ban');bb.setAttribute('data-t',s.sid);d.appendChild(bb);}
}
box.appendChild(d);}}
/* ===== ПУНКТ 3: счётчик неудачных попыток входа — просмотр и ручной сброс (супер) =====
   Сброс подтверждается повторным вводом показанного значения: сервер сверяет его
   с текущим уровнем и при несовпадении сброс НЕ выполняет. */
var failLevel=0;var failDevices=0;
function loadFailState(){if(!isSuper||!token)return;var x=new XMLHttpRequest();
x.open('GET','/adminfail',true);setTok(x);
x.onload=function(){if(x.status!=200)return;var r={};try{r=JSON.parse(x.responseText);}catch(e){return;}
failLevel=(+r.level)||0;failDevices=(+r.devices)||0;
var w=(+r.wait)||0;var nx=(+r.pause)||0;var t;
if(failDevices<=0&&failLevel<=0){t='Счётчик пуст — сбрасывать нечего.';}
else{t='Устройств: '+failDevices+' | уровень паузы: '+failLevel+' | следующая пауза: '+fmtWait(nx);
if(w>0){t+=' | остаток самой длинной паузы: '+fmtWait(w);}}
setTxt(el('failInfo'),t);};
x.onerror=function(){};x.send();}
function openFailModal(){if(failDevices<=0&&failLevel<=0){toastO('Счётчик пуст — сбрасывать нечего',false);return;}
setTxt(el('failPrompt'),'Устройств в счётчике: '+failDevices+'.\nТекущий уровень паузы: '+failLevel+'.\n\nСбрасывается уровень по ВСЕМ устройствам:\nследующая неудачная попытка снова даст паузу 30 сек.\n\nДля подтверждения введите это значение ещё раз:  '+failLevel);
el('mFailV').value='';setTxt(el('mFailE'),'');el('mFailO').className='modalO on';
setTimeout(function(){el('mFailV').focus();},50);}
function closeFailModal(){el('mFailO').className='modalO';}
function submitFailModal(){var v=(''+el('mFailV').value).replace(/^\s+|\s+$/g,'');
if(v!==(''+failLevel)){setTxt(el('mFailE'),'Значение не совпало. Введите: '+failLevel);return;}
var x=new XMLHttpRequest();x.open('GET','/adminresetfails?confirm='+encodeURIComponent(v),true);setTok(x);
x.onload=function(){var r={};try{r=JSON.parse(x.responseText);}catch(e){}
if(x.status==200&&r.ok==1){closeFailModal();toastO('Счётчик попыток входа сброшен (устройств: '+((+r.reset)||0)+')',true);loadFailState();}
else if(x.status==403){closeFailModal();toastO('Недостаточно прав для сброса',false);}
else{failLevel=(+r.level)||failLevel;setTxt(el('mFailE'),'Сброс отклонён. Актуальное значение: '+failLevel);}};
x.onerror=function(){setTxt(el('mFailE'),'Нет связи с сервером');};x.send();}
el('bFailReset').onclick=openFailModal;
el('mFailC').onclick=closeFailModal;
el('mFailOk').onclick=submitFailModal;
el('mFailV').onkeydown=function(ev0){var v=ev0||window.event;
if(v.key==='Enter'||v.keyCode===13||v.which===13){if(v.preventDefault){v.preventDefault();}submitFailModal();}};
/* ===== ПУНКТ 12: АРХИВ ВРЕМЕНИ (6 последних стопов) =====
   Слепок каждого стопа: ВРЕМЯ МАТЧА, ВРЕМЯ АКТИВНЫХ ПОЗИЦИЙ, СЧЁТ, ПЕРИОД.
   Данные приходят в state.json в поле «arch» (новые записи первыми).
   «Применить выбранное» отправляет arch_apply с индексом записи - десктоп и веб
   используют один и тот же код применения (Patch_ApplySnapshotCore). */
var archSig='';var archSel=-1;var archCache=[];
function archFind(idx){for(var i=0;i<archCache.length;i++){if((+archCache[i].idx)==(+idx))return archCache[i];}return null;}
function markArch(){var box=el('archList');if(!box)return;var ch=box.children;
for(var i=0;i<ch.length;i++){var c=ch[i];
if(!c.getAttribute||!c.getAttribute('data-i'))continue;
c.className=((+c.getAttribute('data-i'))==archSel)?'arow sel':'arow';}}
function archPens(r){var ph=(''+(r.pen_h||'')),pa=(''+(r.pen_a||''));
if(!ph&&!pa)return 'нет';
var out='';if(ph){out+='ХОЗ '+ph;}if(ph&&pa){out+='   |   ';}if(pa){out+='ГОС '+pa;}return out;}
function renderArch(s){var box=el('archList');if(!box)return;
var a=(s&&s.arch)?s.arch:[];archCache=a;
var sig='';for(var i=0;i<a.length;i++){var q=a[i];
sig+=q.n+';'+q.t+';'+q.period+';'+q.score_h+';'+q.score_a+';'+q.pen_h+';'+q.pen_a+'|';}
if(sig===archSig){markArch();return;}
archSig=sig;archSel=-1;box.innerHTML='';
if(a.length===0){var d0=document.createElement('div');d0.className='empty';setTxt(d0,'нет записей');box.appendChild(d0);return;}
for(var k=0;k<a.length;k++){var r=a[k];
var d=document.createElement('div');d.className='arow';d.setAttribute('data-i',r.idx);
var l1=document.createElement('div');l1.className='ar1';
setTxt(l1,(r.n||'')+'    ВРЕМЯ МАТЧА '+fmts(r.t)+'    ПЕРИОД '+((+r.period>0)?r.period:'—')+'    СЧЁТ '+((+r.score_h)||0)+':'+((+r.score_a)||0));
var l2=document.createElement('div');l2.className='ar2';
setTxt(l2,'АКТИВНЫЕ ПОЗИЦИИ: '+archPens(r));
d.appendChild(l1);d.appendChild(l2);
d.onclick=function(){var v=+this.getAttribute('data-i');archSel=(archSel===v)?-1:v;markArch();};
box.appendChild(d);}
markArch();}
function openArchModal(){if(archSel<0){toastO('Сначала выберите запись архива',false);return;}
var r=archFind(archSel);if(!r){toastO('Запись архива не найдена - список обновился',false);archSel=-1;markArch();return;}
setTxt(el('archPrompt'),(r.n||'')+'\n\nЧто произойдёт:\n- часы периода остановятся;\n- остаток времени периода станет '+fmts(r.t)+';\n- ТЕКУЩИЕ удаления будут заменены удалениями из снимка.\n\nСчёт и НОМЕР периода НЕ изменятся.');
el('mArchO').className='modalO on';}
function closeArchModal(){el('mArchO').className='modalO';}
el('bArchApply').onclick=openArchModal;
el('mArchC').onclick=closeArchModal;
el('mArchOk').onclick=function(){closeArchModal();var ai=archSel;archSel=-1;markArch();
if(ai<0){return;}
sendCmd('arch_apply',ai);toastO('Запрос на применение снимка отправлен',true);};
/* ===== БЛОК 6, З1: Пробел = Старт/Стоп + подсветка кнопок ===== */
function updSS(){var b1=el('bStart'),b2=el('bStop');
if(b1){if(RUN){b1.style.background='#c00000';b1.style.borderColor='#e08080';}else{b1.style.background='#000000';b1.style.borderColor='#666';}}
if(b2){if((!RUN)&&ELAPS>0){b2.style.background='#0055cc';b2.style.borderColor='#88aaff';}else{b2.style.background='';b2.style.borderColor='';}}}
addEv(document,'keydown',function(e){var ev=e||window.event;
if(ev.keyCode==32||ev.key===' '){if(!token)return;
var mr=el('mResetO'),mm=el('mRemO'),mf=el('mFailO'),ma=el('mArchO');
if((mr&&mr.className.indexOf('on')>=0)||(mm&&mm.className.indexOf('on')>=0)||(mf&&mf.className.indexOf('on')>=0)||(ma&&ma.className.indexOf('on')>=0))return;
var a=document.activeElement;if(a&&(a.tagName==='BUTTON'||a.tagName==='INPUT'||a.tagName==='TEXTAREA'))return;
if(ev.preventDefault){ev.preventDefault();}ev.returnValue=false;
cmd(RUN?'stop':'start');}});
/* ===== ПУНКТ 4: СПИСОК АКТИВНЫХ ПОЗИЦИЙ И СНЯТИЕ КОНКРЕТНОЙ СТРОКИ =====
   БЫЛО: строка идентифицировалась парой (команда, номер), а сервер снимал все строки
   с этим номером у ОБЕИХ команд — при одинаковых номерах снимались обе.
   СТАЛО: каждая строка знает свою команду и свой ИНДЕКС (клетку табло 1×1/1×2/2×1/2×2),
   снятие идёт командой pen_rem_row_* и убирает ровно одну строку.
   Логика одна и та же в обоих местах:
     - в списке активных позиций: нажал строку -> «СНЯТЬ» -> снято;
     - если строка не выбрана: «СНЯТЬ» -> окно 2×2 (или ввод номера) -> подтверждение. */
function rowsOf(str,team){var out=[];var arr=(''+(str||'')).split('; ');
for(var i=0;i<arr.length;i++){var t=arr[i];if(!t)continue;
var n=parseInt(t.substring(0,2),10);
out.push({team:team,idx:out.length,num:isNaN(n)?0:n,raw:t,
cell:(out.length+1)+'×'+(team=='h'?1:2),
text:(out.length+1)+'×'+(team=='h'?1:2)+' '+(team=='h'?'ХОЗ':'ГОС')+' '+t});}
return out;}
function rowAt(team,idx){var src=(team=='h')?rowsCache.h:rowsCache.a;
for(var i=0;i<src.length;i++){if(src[i].idx==idx)return src[i];}
return null;}
var rowsCache={h:[],a:[]};
function markSelO(){var box=el('penList');if(!box)return;var ch=box.children;
for(var i=0;i<ch.length;i++){var c=ch[i];
if(penSel&&c.getAttribute('data-t')==penSel.team&&(+c.getAttribute('data-i'))==penSel.idx){c.className='sel';}
else{c.className='';}}}
function renderPenList(s){var box=el('penList');if(!box)return;
rowsCache.h=rowsOf(s.pen_h,'h');rowsCache.a=rowsOf(s.pen_a,'a');
var rows=rowsCache.h.concat(rowsCache.a);
var sig='';for(var i=0;i<rows.length;i++){sig+=rows[i].team+rows[i].idx+rows[i].text+'|';}
if(penSel){if(!rowAt(penSel.team,penSel.idx)||(rowAt(penSel.team,penSel.idx).num!=penSel.num)){penSel=null;penListSig='';}}
if(sig==penListSig){return;}
penListSig=sig;
box.innerHTML='';
if(rows.length==0){var d0=document.createElement('div');d0.className='empty';setTxt(d0,'нет удалений');box.appendChild(d0);return;}
for(var k=0;k<rows.length;k++){var r=rows[k];var d=document.createElement('div');setTxt(d,r.text);
d.setAttribute('data-t',r.team);d.setAttribute('data-i',r.idx);d.setAttribute('data-n',r.num);
d.onclick=penRowClick;box.appendChild(d);}
markSelO();}
function penRowClick(){var t=this.getAttribute('data-t');var i=+this.getAttribute('data-i');var n=+this.getAttribute('data-n');
if(penSel&&penSel.team==t&&penSel.idx==i){penSel=null;}else{penSel={team:t,idx:i,num:n};}
markSelO();}
/* Проверка результата снятия: ждём два опроса и смотрим, ушла ли именно эта строка. */
function tickPendRem(s){if(!pendRemO)return;pendRemO.wait--;
if(pendRemO.wait>0)return;
var rows=(pendRemO.team=='h')?rowsOf(s.pen_h,'h'):rowsOf(s.pen_a,'a');
var tm=(pendRemO.team=='h')?'хозяева':'гости';var still=false;
if(pendRemO.byRow){var r=null;for(var i=0;i<rows.length;i++){if(rows[i].idx==pendRemO.idx){r=rows[i];break;}}
still=(r&&r.num==pendRemO.num);}
else{for(var j=0;j<rows.length;j++){if(rows[j].num==pendRemO.num){still=true;break;}}}
if(still){toastO('Не снято: '+tm+', №'+pendRemO.num+(pendRemO.byRow?(', строка '+(pendRemO.idx+1)):'')+' — всё ещё на месте',false);}
else{toastO('Снято: '+tm+', №'+pendRemO.num+(pendRemO.byRow?(', строка '+(pendRemO.idx+1)):''),true);}
pendRemO=null;}
/* Снятие конкретной строки (клетки табло) */
function doRemRowO(team,idx,num){pendRemO={team:team,idx:idx,num:num,wait:2,byRow:true};
sendCmd(team=='h'?'pen_rem_row_h':'pen_rem_row_a',idx*100+num);}
/* Снятие по номеру — только у выбранной команды, первая строка этого номера */
function doRemNumO(team,num){pendRemO={team:team,idx:-1,num:num,wait:2,byRow:false};
sendCmd(team=='h'?'pen_rem_h':'pen_rem_a',num);}
/* ----- окно снятия 2×2 ----- */
var remSel='';       // 'h0','a0','h1','a1' — выбранная клетка
var remTeamByNum='h';// команда для снятия по номеру
function fillRemGrid(){
var keys=[['h',0,'rc_h0'],['a',0,'rc_a0'],['h',1,'rc_h1'],['a',1,'rc_a1']];
for(var i=0;i<keys.length;i++){var b=el(keys[i][2]);if(!b)continue;
var r=rowAt(keys[i][0],keys[i][1]);
var sp=b.getElementsByTagName('span');
if(sp.length>1){setTxt(sp[1],r?r.raw:'—');}
var key=keys[i][0]+keys[i][1];
b.className='remCell'+(r?' busy':' emptyCell')+(remSel==key?' sel':'');
b.disabled=!r;}}
function remCellClick(){var id=this.id;var t=id.charAt(3);var i=+id.charAt(4);
remSel=(remSel==t+i)?'':(t+i);
if(remSel){el('mRemON').value='';}
setTxt(el('mRemOE'),'');fillRemGrid();}
function openRemModal(){remSel='';remTeamByNum=penTeam;
el('mRemTH').className='team'+(remTeamByNum=='h'?' on':'');
el('mRemTA').className='team'+(remTeamByNum=='a'?' on':'');
el('mRemON').value=el('pn').value;setTxt(el('mRemOE'),'');
fillRemGrid();el('mRemO').className='modalO on';}
function closeRemModal(){el('mRemO').className='modalO';}
function submitRemModal(){
if(remSel){var t=remSel.charAt(0);var i=+remSel.substring(1);var r=rowAt(t,i);
if(!r){setTxt(el('mRemOE'),'Строка уже не активна — выберите другую клетку');fillRemGrid();return;}
closeRemModal();doRemRowO(t,i,r.num);return;}
var n=parseInt(el('mRemON').value,10);
if(isNaN(n)||n<1||n>99){setTxt(el('mRemOE'),'Номер: целое число 1-99 (или выберите клетку выше)');return;}
closeRemModal();doRemNumO(remTeamByNum,n);}
/* Список активных позиций: нажал строку -> «СНЯТЬ» -> снято. Не выбрал -> окно 2×2. */
el('bPenRemSel').onclick=function(){
if(penSel){var t=penSel.team,i=penSel.idx,n=penSel.num;penSel=null;markSelO();doRemRowO(t,i,n);}
else{openRemModal();}};
el('mRemOC').onclick=closeRemModal;
el('mRemOOk').onclick=submitRemModal;
el('rc_h0').onclick=remCellClick;el('rc_a0').onclick=remCellClick;
el('rc_h1').onclick=remCellClick;el('rc_a1').onclick=remCellClick;
el('mRemTH').onclick=function(){remTeamByNum='h';remSel='';el('mRemON').value='';
this.className='team on';el('mRemTA').className='team';fillRemGrid();};
el('mRemTA').onclick=function(){remTeamByNum='a';remSel='';el('mRemON').value='';
this.className='team on';el('mRemTH').className='team';fillRemGrid();};
el('mRemON').onkeydown=function(ev0){var v=ev0||window.event;if(v.key==='Enter'||v.keyCode===13||v.which===13){if(v.preventDefault){v.preventDefault();}submitRemModal();}};
el('mRemON').oninput=function(){if(remSel){remSel='';fillRemGrid();}};
/* ===== БЛОК 6, З2: подтверждение мастер-сброса ===== */
function openResetO(){el('mResetO').className='modalO on';}
function closeResetO(){el('mResetO').className='modalO';}
el('mResetOC').onclick=closeResetO;
el('mResetOOk').onclick=function(){closeResetO();cmd('reset');toastO('Мастер-сброс выполнен',true);};
/* ===== ПУНКТ 5/11: чекбокс «Время матча» и кнопка «Выставить время» УБРАНЫ =====
   Раньше ввод времени периода разрешался только после установки чекбокса, из-за чего
   ручная корректировка во время матча в веб-панели не работала. Теперь поле «Время
   матча» и кнопка «СОХРАНИТЬ ВРЕМЯ» доступны всегда (см. cmd('time_set')).
   Запуск/остановка заставки - кнопки «Запуск заставки и таймера» и
   «Остановить заставку и таймер» (data-k='prestart_arm' / 'splash_hide'). */
el('teamH').onclick=function(){penTeam='h';this.className='team on';el('teamA').className='team';};
el('teamA').onclick=function(){penTeam='a';this.className='team on';el('teamH').className='team';};
el('viewToggle').onclick=toggleView;
el('viewToggleLogin').onclick=toggleView;
addEv(document,'click',function(e){var ev2=e||window.event;var b=ev2.target||ev2.srcElement;
if(b&&b.getAttribute&&b.getAttribute('data-k')){var k=b.getAttribute('data-k');
if(k=='__login'){doLogin();}
else if(k=='__logout'){doLogout();}
else if(k=='names_set'){cmdText('names_set',[uenc(el('nh').value),uenc(el('th').value),uenc(el('na').value),uenc(el('ta').value)].join('|'));}
else if(k=='pen_q'){var nq=parseInt(el('pn').value,10);if(isNaN(nq)||nq<1||nq>99){show('Номер игрока должен быть от 1 до 99');return;}var mq=b.getAttribute('data-m');if(mq=='1.5'){sendCmd(penTeam=='h'?'pen_add_sec_h':'pen_add_sec_a',nq*10000+90);}else{sendCmd(penTeam=='h'?'pen_add_h':'pen_add_a',nq*100+(+mq));}}
else if(k=='speed_set'){sendCmd('speed_set',+b.getAttribute('data-v'));}
else if(k=='admin_kick'||k=='admin_ban'){var u=(k=='admin_kick'?'/adminkick?target=':'/adminban?target=')+encodeURIComponent(b.getAttribute('data-t'));
var x=new XMLHttpRequest();x.open('GET',u,true);setTok(x);x.onload=function(){adminPoll();};x.send();}
else if(k=='reset'){openResetO();}
else{cmd(k);}}});
doPoll();
function opEnter(id,fn){var e=el(id);if(!e)return;e.onkeydown=function(ev){var v=ev||window.event;
if(v.key==='Enter'||v.keyCode===13||v.which===13){var a=document.activeElement;if(a&&a.tagName==='BUTTON')return;
if(v.preventDefault){v.preventDefault();}fn();}};}
opEnter('t',function(){cmd('time_set',mmss(el('t').value));});
opEnter('gm',function(){cmd('cfg_main',mmss(el('gm').value));});
opEnter('ot',function(){cmd('cfg_ot',mmss(el('ot').value));});
opEnter('brk',function(){cmd('break_set',mmss(el('brk').value));});
opEnter('ms',function(){cmd('match_start_set',mmss(el('ms').value));});
opEnter('rm',function(){cmd('ref_edit',mmss(el('rm').value)+'|'+mmss(el('ro').value));});
opEnter('ro',function(){cmd('ref_edit',mmss(el('rm').value)+'|'+mmss(el('ro').value));});
var refKey='';var refDirty=false;
el('rm').oninput=function(){refDirty=true;};
el('ro').oninput=function(){refDirty=true;};
/* ПУНКТ 7: кнопка перехода к пульту оператора теперь в самой разметке topbar
   (раньше создавалась скриптом с собственными инлайн-стилями — из-за этого она
   отличалась по размеру от «Мобильный вид» и «Выйти»). */
el('goPanel').onclick=function(){location.href='/panel';};
boot();
</script>
</body>
</html>";
	}
    }
	public static class ExtOut
	{
		public static Func<Tablo.TConfig> CfgProvider;
		public static Func<string> StateProvider;
		public static Action<TimeSpan> BeforeTotalSetter;
		public static Action<string, string> UiPush;
		private static System.Windows.Forms.Timer _timer;
		private static volatile SerialPort _port;   // БЛОК 3, F2-05: открывается в фоновом потоке
		private static System.Net.Sockets.UdpClient _udp;
		private static bool _netErrLogged = false;
		private static string _lastLine = "";
		private static int _signalTicks = 0;
		private static string _signalTarget = "-";
		private static int _prevBreakRem = -1;
		private static int _prevPreRem = -1;
		private static string _prevMode = "";
		private static string _lastBrkValue = "";
		private static int _openFailCooldown = 0;
		private static int _opening = 0;   // БЛОК 3, F2-05: 1 = прямо сейчас идёт фоновое открытие порта
		private static bool _parseErrLogged = false;
		public static string LastLine = "";

		public static void EnsureStarted()
		{
			if (_timer == null)
			{
				_timer = new System.Windows.Forms.Timer();
				_timer.Interval = 1000;
				_timer.Tick += Tick;
			}
			_timer.Start();
		}

		public static void StopAll()
		{
			if (_timer != null)
			{
				_timer.Stop();
			}
			ClosePort();
		}

		private static void ClosePort()
		{
			if (_port != null)
			{
				try
				{
					_port.Close();
				}
				catch
				{
				}
				try
				{
					_port.Dispose();
				}
				catch
				{
				}
				_port = null;
			}
		}

		private static bool CrossMark(int prev, int cur, int mark)
		{
			return prev >= 0 && cur >= 0 && mark > 0 && prev > mark && cur <= mark;
		}

		private static string ApplyTemplate(string tpl, string mode, string time, string sig, string to)
		{
			string s = (tpl == null || tpl.Trim().Length == 0) ? "EXT;{MODE};{TIME};{SIG};{TO}" : tpl;
			s = s.Replace("{MODE}", mode);
			s = s.Replace("{TIME}", time);
			s = s.Replace("{SIG}", sig);
			s = s.Replace("{TO}", to);
			return s;
		}

		private static int ParseTimeValue(string s)
		{
			try
			{
				string[] p = s.Split(new char[] { ':' });
				if (p.Length == 2)
				{
					return int.Parse(p[0]) * 60 + int.Parse(p[1]);
				}
				if (p.Length == 3)
				{
					return int.Parse(p[0]) * 3600 + int.Parse(p[1]) * 60 + int.Parse(p[2]);
				}
				return -1;
			}
			catch
			{
				return -1;
			}
		}

		private static int ParseMMSS(string s)
		{
			try
			{
				string t = (s == null) ? "" : s.Trim();
				int i = t.IndexOf(':');
				if (i <= 0 || i >= t.Length - 1)
				{
					return -1;
				}
				int mm = int.Parse(t.Substring(0, i));
				int ss = int.Parse(t.Substring(i + 1));
				if (ss < 0 || ss > 59)
				{
					return -1;
				}
				return mm * 60 + ss;
			}
			catch
			{
				return -1;
			}
		}

		private static void PushUi(string line, string svc)
		{
			try
			{
				if (UiPush != null)
				{
					UiPush(line, svc);
				}
			}
			catch
			{
			}
		}

		private static void Svc(string msg)
		{
			SerialPortLog.Write("ДОП.ВЫВОД: " + msg);
			PushUi(null, msg);
		}

		public static void TestFire(string target, string kind)
		{
			Fire(target, kind);
		}

		private static void Fire(string target, string kind)
		{
			_signalTicks = 3;
			_signalTarget = target;
			Svc("СИГНАЛ: " + kind + " -> " + target);
		}

		private static void Tick(object sender, EventArgs e)
		{
			try
			{
				if (CfgProvider == null || StateProvider == null)
				{
					return;
				}
				Tablo.TConfig cfg = CfgProvider();
				if (cfg == null)
				{
					return;
				}
				bool portWanted = cfg.ExtEnable && !string.IsNullOrEmpty(cfg.ExtPort);
				if (!portWanted)
				{
					ClosePort();
				}
				int baud = 9600;
				try
				{
					baud = int.Parse(cfg.ExtBaud);
				}
				catch
				{
				}
				if (portWanted && _port == null && _openFailCooldown > 0)
				{
					_openFailCooldown--;
				}
				else if (portWanted && (_port == null || _port.PortName != cfg.ExtPort || _port.BaudRate != baud))
				{
					ClosePort();
					// БЛОК 3, F2-05: SerialPort.Open() на "подвисшем" USB-COM может блокировать
					// на секунды, а Tick идёт на UI-потоке (интерфейс замерзал бы). Открываем В ФОНЕ;
					// Tick просто продолжит работу на следующем цикле, когда _port появится.
					if (System.Threading.Interlocked.CompareExchange(ref _opening, 1, 0) == 0)
					{
						string pn = cfg.ExtPort;
						int bd = baud;
						System.Threading.ThreadPool.QueueUserWorkItem(delegate(object st)
						{
							SerialPort sp = null;
							try
							{
								sp = new SerialPort(pn, bd, Parity.None, 8, StopBits.One);
								sp.WriteTimeout = 500;
								sp.Open();
							}
							catch (Exception ex)
							{
								try
								{
									if (sp != null)
									{
										sp.Dispose();
									}
								}
								catch
								{
								}
								sp = null;
								_openFailCooldown = 30;
								Svc("порт " + pn + " не открыт: " + ex.Message + ", повтор через 30 сек");
							}
							if (sp != null)
							{
								_port = sp;
								_openFailCooldown = 0;
								Svc("порт " + pn + " открыт (в фоне)");
							}
							System.Threading.Interlocked.Exchange(ref _opening, 0);
						});
					}
				}
				string snap = StateProvider();
				string[] parts = snap.Split(new char[] { '|' });
				string mode = parts[0];
				string value = parts[1];
				string sig = "0";
				string target = "-";
				if (mode != _prevMode)
				{
					Svc("ПЕРЕХОД РЕЖИМА: " + ((_prevMode == null || _prevMode == "") ? "(старт)" : _prevMode) + " -> " + mode + ", значение " + value);
					if (_prevMode == "BRK" && _lastBrkValue == "00:00")
					{
						Fire("A", "КОНЕЦ ПЕРЕРЫВА");
						sig = "1";
						target = "A";
					}
					_prevMode = mode;
					_prevBreakRem = -1;
					_prevPreRem = -1;
					_lastBrkValue = "";
					_parseErrLogged = false;
				}
				int remSec = -1;
				if (mode == "BRK" || mode == "PRE")
				{
					remSec = ParseTimeValue(value);
					if (remSec < 0 && !_parseErrLogged)
					{
						_parseErrLogged = true;
						Svc("не разобрал значение " + value + ", метки приостановлены до смены режима");
					}
				}
				if (mode == "BRK")
				{
					_lastBrkValue = value;
				}
				if (_signalTicks > 0)
				{
					_signalTicks--;
					sig = "1";
					target = _signalTarget;
				}
				else if (cfg.ExtSignals)
				{
					string fired = null;
					string kind = null;
					if (mode == "BRK")
					{
						int brk1 = ParseMMSS(cfg.BrkMark1);
						if (brk1 < 0)
						{
							brk1 = 180;
						}
						if (CrossMark(_prevBreakRem, remSec, brk1))
						{
							fired = "A";
							kind = "ПЕРЕРЫВ: за " + (brk1 / 60) + " мин до конца";
						}
						_prevBreakRem = remSec;
					}
					else if (mode == "PRE")
					{
						int[] pre = new int[4];
						pre[0] = ParseMMSS(cfg.PreMark1);
						if (pre[0] < 0)
						{
							pre[0] = 3600;
						}
						pre[1] = ParseMMSS(cfg.PreMark2);
						if (pre[1] < 0)
						{
							pre[1] = 2400;
						}
						pre[2] = ParseMMSS(cfg.PreMark3);
						if (pre[2] < 0)
						{
							pre[2] = 1200;
						}
						pre[3] = ParseMMSS(cfg.PreMark4);
						if (pre[3] < 0)
						{
							pre[3] = 120;
						}
						string[] tg = new string[] { "A", "A", "A", "A" };
						for (int i = 0; i < 4; i++)
						{
							if (CrossMark(_prevPreRem, remSec, pre[i]))
							{
								fired = tg[i];
								kind = "ПРЕДМАТЧ: метка " + (i + 1);
								break;
							}
						}
						_prevPreRem = remSec;
					}
					if (fired != null)
					{
						Fire(fired, kind + ", остаток " + value);
						sig = "1";
						target = fired;
					}
				}
				string line = ApplyTemplate(cfg.ExtFrameTemplate, mode, value, sig, target) + "\r";
				LastLine = line;
				if (line != _lastLine || sig == "1")
				{
					_lastLine = line;
					if (_port != null)
					{
						try
						{
							_port.Write(line);
						}
						catch (Exception ex)
						{
							Svc("ошибка записи в порт: " + ex.Message);
						}
					}
				}
				if (cfg.NetEnable)
				{
					try
					{
						if (_udp == null)
						{
							_udp = new System.Net.Sockets.UdpClient();
							_udp.EnableBroadcast = true;
						}
						int netPort = 5005;
						try
						{
							netPort = int.Parse(cfg.NetPort);
						}
						catch
						{
						}
						string host = string.IsNullOrEmpty(cfg.NetHost) ? "255.255.255.255" : cfg.NetHost;
						byte[] data = System.Text.Encoding.UTF8.GetBytes(line);
						_udp.Send(data, data.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Parse(host), netPort));
						_netErrLogged = false;
					}
					catch (Exception ex)
					{
						if (!_netErrLogged)
						{
							_netErrLogged = true;
							Svc("ошибка сетевой отдачи: " + ex.Message);
						}
					}
				}
				else if (_udp != null)
				{
					try
					{
						_udp.Close();
					}
					catch
					{
					}
					_udp = null;
				}
				PushUi(line, null);
			}
			catch (Exception ex)
			{
				Svc("ошибка тика: " + ex.Message);
			}
		}
	}

	public class ExtOutUiForm : Form
	{
		private Label _value;

		public ExtOutUiForm()
		{
			this.Text = "Доп. табло (Ctrl+F8)";
			this.Width = 480;
			this.Height = 240;
			this.MinimumSize = new Size(200, 120);
			this.StartPosition = FormStartPosition.CenterScreen;
			this.BackColor = Color.Black;
			this._value = new Label();
			this._value.Text = "--:--";
			this._value.ForeColor = Color.White;
			this._value.AutoSize = true;
			this._value.Font = UiFonts.Mono(40f, FontStyle.Bold);
			this.Controls.Add(this._value);
			this.Resize += delegate
			{
				this.LayoutValue();
			};
			ExtOut.UiPush += this.OnPush;
			this.FormClosed += delegate
			{
				ExtOut.UiPush -= this.OnPush;
			};
			this.LayoutValue();
		}

		private void OnPush(string line, string svc)
		{
			if (this.InvokeRequired)
			{
				try
				{
					this.BeginInvoke(new Action<string, string>(this.OnPush), new object[] { line, svc });
				}
				catch
				{
				}
				return;
			}
			if (line != null)
			{
				string[] p = line.TrimEnd(new char[] { '\r' }).Split(new char[] { ';' });
				if (p.Length >= 3)
				{
					this._value.Text = p[2];
					this.LayoutValue();
				}
			}
		}

		private void LayoutValue()
		{
			float size = Math.Min(this.ClientSize.Width / 7f, this.ClientSize.Height / 2.2f);
			if (size < 12f)
			{
				size = 12f;
			}
			this._value.Font = UiFonts.Mono(size, FontStyle.Bold);
			this._value.Location = new Point(Math.Max(0, (this.ClientSize.Width - this._value.Width) / 2), Math.Max(0, (this.ClientSize.Height - this._value.Height) / 2));
		}
	}

	public static class UiFonts
	{
		private static string _mono;

		public static string MonoName
		{
			get
			{
				if (_mono == null)
				{
					_mono = "Courier New";
					try
					{
						foreach (System.Drawing.FontFamily f in new System.Drawing.Text.InstalledFontCollection().Families)
						{
							if (f.Name == "Consolas")
							{
								_mono = "Consolas";
								break;
							}
						}
					}
					catch
					{
					}
				}
				return _mono;
			}
		}

		public static Font Mono(float size, FontStyle style)
		{
			return new Font(MonoName, size, style);
		}
	}

	public static class ExtOutUi
	{
		private static ExtOutUiForm _form;

		public static void Open()
		{
			if (_form == null || _form.IsDisposed)
			{
				_form = new ExtOutUiForm();
				_form.Show();
			}
			else
			{
				_form.BringToFront();
			}
		}
	}
    }
