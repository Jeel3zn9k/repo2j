using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using Multimedia;
using SerialPortControlBoard;
using TabloHockey;
using VideoPlayer;

namespace Tablo
{
	// Token: 0x02000008 RID: 8
	public partial class frmMain : Form
	{
		// Token: 0x17000008 RID: 8
		// (get) Token: 0x06000031 RID: 49 RVA: 0x0000229A File Offset: 0x0000049A
		// (set) Token: 0x06000032 RID: 50 RVA: 0x000022A2 File Offset: 0x000004A2
		private int TimeOutCounter_L
		{
			get
			{
				return this._TimeOutCounter_L;
			}
			set
			{
				this._TimeOutCounter_L = value;
				if (this.thf != null)
				{
					this.thf.TimeOutCount_L = (ushort)this._TimeOutCounter_L;
				}
			}
		}

		// Token: 0x17000009 RID: 9
		// (get) Token: 0x06000033 RID: 51 RVA: 0x000022C5 File Offset: 0x000004C5
		// (set) Token: 0x06000034 RID: 52 RVA: 0x000022CD File Offset: 0x000004CD
		private int TimeOutCounter_R
		{
			get
			{
				return this._TimeOutCounter_R;
			}
			set
			{
				this._TimeOutCounter_R = value;
				if (this.thf != null)
				{
					this.thf.TimeOutCount_R = (ushort)this._TimeOutCounter_R;
				}
			}
		}

		// Token: 0x06000035 RID: 53 RVA: 0x00005C9C File Offset: 0x00003E9C
		private void Initial()
	{
		this.gs_Main.EffectIndex = Effects.Clear;
		string text = this._settingForm.cfg.ScriptPlaylistPath;
		if (text != string.Empty)
		{
			try
			{
				string[] items = File.ReadAllLines(text);
				this.cLB_ScriptFiles.Items.Clear();
				this.cLB_ScriptFiles.Items.AddRange(items);
			}
			catch (Exception ex)
			{
				this.cLB_ScriptFiles.Items.Clear();
				// БЛОК 4, F4-12: сообщение по-русски
				MessageBox.Show("Не удалось загрузить список сценариев: " + ex.Message);
			}
		}
		text = this._settingForm.cfg.MediaPlaylistPath;
		if (text != string.Empty)
		{
			this.LoadVideoList(text);
		}
		this._GameMode = frmMain.gameMode.GameStoped;
		this._TabloMode = frmMain.tabloMode.game;
		this.Cmd_ShowPrompt();
		this._gameTimer = new Multimedia.Timer();
		this._gameTimer.Period = 10;
		this._gameTimer.Resolution = 1;
		this._gameTimer.Mode = TimerMode.Periodic;
		this._gameTimer.SynchronizingObject = this;
		this._gameTimer.Tick += this._timerGame_Tick;
		this._timeoutTimer = new Multimedia.Timer();
		this._timeoutTimer.Period = 1000;
		this._timeoutTimer.Resolution = 100;
		this._timeoutTimer.Mode = TimerMode.Periodic;
		this._timeoutTimer.SynchronizingObject = this;
		this._timeoutTimer.Tick += this._timerTimeOut_Tick;
		this._breakTimer = new Multimedia.Timer();
		this._breakTimer.Period = 1000;
		this._breakTimer.Resolution = 100;
		this._breakTimer.Mode = TimerMode.Periodic;
		this._breakTimer.SynchronizingObject = this;
		this._breakTimer.Tick += this._timerBreak_Tick;
		this._WhistleBeep = new TAudioPlayer();
		if (this._WhistleBeep.PrepareSound(Patch_AudioPath(this._settingForm.cfg.WhistleBeepFile)) != 0)
		{
			this._WhistleBeep = null;
		}
		this._GetGateBeep = new TAudioPlayer();
		if (this._GetGateBeep.PrepareSound(Patch_AudioPath(this._settingForm.cfg.GetGateBeepFile)) != 0)
		{
			this._GetGateBeep = null;
		}
		this._GameStatrBeep = new TAudioPlayer();
		this._GameStatrBeep.OnAudioPlayerStop += this._GameStatrBeep_OnAudioPlayerStop;
		if (this._GameStatrBeep.PrepareSound(Patch_AudioPath(this._settingForm.cfg.StartGameBeepFile)) != 0)
		{
			this._GameStatrBeep = null;
		}
		this._BeforeStartPlayer = new TAudioPlayer();
		if (this._BeforeStartPlayer.PrepareSound(Patch_AudioPath(this._settingForm.cfg.BeepFile)) != 0)
		{
			this._BeforeStartPlayer = null;
		}
		this.LoadCommandName();
		this.Init_gameVars();
		this.Init_timeoutVars();
		this.Init_breakVars();
		this.Patch_ProcessPassResetFile();
		TabloPatch.SerialPortLog.Enabled = this._settingForm.cfg.RemoteLogEnable;
		TabloPatch.PatchEntry.Init(null);
		TabloPatch.ExtOut.CfgProvider = delegate
		{
			return this._settingForm.cfg;
		};
		TabloPatch.ExtOut.StateProvider = new Func<string>(this.Patch_ExternalSnapshot);
		TabloPatch.ExtOut.BeforeTotalSetter = new Action<TimeSpan>(this.Patch_SetBeforeTotal);
		TabloPatch.ExtOut.EnsureStarted();
		TabloPatch.WebServer.PassHash = this._settingForm.cfg.WebPassHash;
		// БЛОК 4, F7-03: при успешном веб-входе со старым SHA-1-хэшем сервер создаёт
		// PBKDF2-хэш — этот колбэк сохраняет его в config.xml (прозрачная миграция).
		TabloPatch.WebServer.PassHashUpgraded = delegate(string newHash)
		{
			try
			{
				if (this._settingForm != null && this._settingForm.cfg != null)
				{
					this._settingForm.cfg.WebPassHash = newHash;
					this._settingForm.cfg.Save();
				}
			}
			catch (Exception exUp)
			{
				TabloPatch.Diag.Note("Сохранение апгрейда WebPassHash", exUp);
			}
		};
		// ===== БЛОК 4, F7-01: авто-«починка» подписи включения УБРАНА =====
		// Раньше при любом рассинхроне программа САМА перевыпускала токен включения
		// веб-сервера — фактически включала сервер за спиной администратора (даже
		// когда пароль разработчика пуст). Теперь сервер просто не стартует,
		// а причина честно пишется в лог.
		if (this._settingForm.cfg.WebEnable == "1" && !TabloPatch.DevGuard.WebEnabled(this._settingForm.cfg))
		{
			TabloPatch.SerialPortLog.Write("WEB: сервер НЕ запущен — подпись включения отсутствует/недействительна или не задан пароль разработчика. Откройте «Настройки -> Режим разработчика», включите сервер заново и нажмите «Применить».");
		}
		if (TabloPatch.DevGuard.WebEnabled(this._settingForm.cfg))
		{
			int wp = 8642;
			try
			{
				wp = int.Parse(this._settingForm.cfg.WebPort);
			}
			catch
			{
			}
			if (wp < 1024 || wp > 65535)
			{
				wp = 8642;
			}
			TabloPatch.WebServer.Start(wp, this._settingForm.cfg.WebBindAll == "1", this._settingForm.cfg.WebIp, this._settingForm.cfg.WebUseHttps == "1");
		}
		else
		{
			TabloPatch.SerialPortLog.Write("WEB: сервер выключен в режиме разработчика");
		}
				TabloPatch.WebServer.CmdProvider = delegate(string key, int p)
		{
			// БЛОК 3, F3-09: честный результат команды. Возвращаем КОД:
			// 0 = команда принята, 1 = неизвестная команда, 2 = отклонено защитой (идёт время периода, Q6),
			// 3 = внутренний сбой. Панель показывает причину вместо общего "отклонено".
			TabloPatch.RemoteCommand c = new TabloPatch.RemoteCommand();
			switch (key)
			{
				case "start": c.Cmd = TabloPatch.RemoteCmd.WhistleStart; break;
				case "stop": c.Cmd = TabloPatch.RemoteCmd.WhistleStop; break;
				case "whistle": c.Cmd = TabloPatch.RemoteCmd.WhistleBeep; break;
				case "break":
					if (this._GameMode == frmMain.gameMode.GameStarted && !TabloPatch.WebServer.GodMode) { return 2; }
					c.Cmd = TabloPatch.RemoteCmd.Intermission; break;
				case "timeout_h":
					if (this._GameMode == frmMain.gameMode.GameStarted && !TabloPatch.WebServer.GodMode) { return 2; }
					c.Cmd = TabloPatch.RemoteCmd.TimeoutHome; break;
				case "timeout_a":
					if (this._GameMode == frmMain.gameMode.GameStarted && !TabloPatch.WebServer.GodMode) { return 2; }
					c.Cmd = TabloPatch.RemoteCmd.TimeoutAway; break;
				case "splash_hide": c.Cmd = TabloPatch.RemoteCmd.SplashHide; break;
				case "reset": c.Cmd = TabloPatch.RemoteCmd.MasterReset; break;
				case "period_next": c.Cmd = TabloPatch.RemoteCmd.PeriodNext; break;
				case "period_prev": c.Cmd = TabloPatch.RemoteCmd.PeriodPrev; break;
				case "time_set": c.Cmd = TabloPatch.RemoteCmd.TimeSetSave; c.TimeSec = p; break;
				case "score_h_plus": c.Cmd = TabloPatch.RemoteCmd.ScoreHomePlus; break;
				case "score_h_minus": c.Cmd = TabloPatch.RemoteCmd.ScoreHomeMinus; break;
				case "score_a_plus": c.Cmd = TabloPatch.RemoteCmd.ScoreAwayPlus; break;
				case "score_a_minus": c.Cmd = TabloPatch.RemoteCmd.ScoreAwayMinus; break;
				case "pen_add_h": c.Cmd = TabloPatch.RemoteCmd.PenaltyAdd; c.HomeTeam = true; c.PlayerNumber = p / 100; c.Minutes = p % 100; break;
				case "pen_add_a": c.Cmd = TabloPatch.RemoteCmd.PenaltyAdd; c.HomeTeam = false; c.PlayerNumber = p / 100; c.Minutes = p % 100; break;
				// ================== ПУНКТ 4: СНЯТИЕ КОНКРЕТНОЙ СТРОКИ ==================
				// БЫЛО: обе команды шли через RemoteCmd.PenaltyRemovePlayer, а обработчик
				// Patch_RemovePenalized() вычищал строки с этим номером из ОБОИХ списков —
				// при одинаковых номерах у хозяев и гостей снимались обе.
				// СТАЛО: веб-снятие обрабатывается отдельно и только в списке СВОЕЙ команды,
				// причём можно снять КОНКРЕТНУЮ строку (клетку табло 1×1 / 1×2 / 2×1 / 2×2).
				// Команды физического пульта (DelGamer) и эмулятор не затронуты: они
				// по-прежнему идут своими путями.
				//	pen_rem_h / pen_rem_a          -> p = номер игрока (1..99), снимается
				//	                                  ПЕРВАЯ строка этого номера у команды;
				//	pen_rem_row_h / pen_rem_row_a  -> p = slot*100 + номер, slot = 0|1
				//	                                  (строка на табло), снимается ровно она.
				case "pen_rem_h":
				case "pen_rem_a":
				case "pen_rem_row_h":
				case "pen_rem_row_a":
				{
					string keyRem = key;
					int pRem = p;
					if (this.InvokeRequired)
					{
						this.BeginInvoke(new System.Windows.Forms.MethodInvoker(delegate { this.Patch_WebRemovePenalty(keyRem, pRem); }));
						return 0;
					}
					this.Patch_WebRemovePenalty(keyRem, pRem);
					return 0;
				}

				// ПУНКТ 12: применение снимка из архива времени (p = индекс записи).
				case "arch_apply":
				{
					int ai = p;
					if (this.InvokeRequired)
					{
						this.BeginInvoke(new System.Windows.Forms.MethodInvoker(delegate { this.Patch_WebArchiveApply(ai); }));
						return 0;
					}
					this.Patch_WebArchiveApply(ai);
					return 0;
				}
				case "pen_add_sec_h": c.Cmd = TabloPatch.RemoteCmd.PenaltyAdd; c.HomeTeam = true; c.PlayerNumber = p / 10000; c.TimeSec = p % 10000; break;
				case "pen_add_sec_a": c.Cmd = TabloPatch.RemoteCmd.PenaltyAdd; c.HomeTeam = false; c.PlayerNumber = p / 10000; c.TimeSec = p % 10000; break;
				case "cfg_main":
				case "cfg_ot":
				case "break_set":
				case "speed_set":
				case "ref_remember":
				case "god_toggle":
				case "to_h_dec":
				case "to_a_dec":
				case "match_start_set":
				case "prestart_arm":
					if (this.InvokeRequired)
					{
						this.BeginInvoke(new System.Windows.Forms.MethodInvoker(delegate { this.Patch_WebTimeCmd(key, p); }));
						return 0;
					}
					this.Patch_WebTimeCmd(key, p);
					return 0;
				default: return 1;
			}
			if (this.InvokeRequired)
			{
				this.BeginInvoke(new System.Windows.Forms.MethodInvoker(delegate { this.Patch_DispatchRemoteCommand(c); }));
				return 0;
			}
			this.Patch_DispatchRemoteCommand(c);
			return 0;
		};
		TabloPatch.WebServer.CmdTextProvider = delegate(string key, string v)
		{
			if (key == "ref_edit")
			{
				string[] qq = (v ?? "").Split(new char[] { '|' });
				if (qq.Length != 2)
				{
					return false;
				}
				int m = 0;
				int o = 0;
				int.TryParse(qq[0], out m);
				int.TryParse(qq[1], out o);
				int mm = m;
				int oo = o;
				if (this.InvokeRequired)
				{
					this.BeginInvoke(new System.Windows.Forms.MethodInvoker(delegate
					{
						TabloPatch.PatchEntry.Core.ManualEditSavedMainTime(mm);
						TabloPatch.PatchEntry.Core.ManualEditSavedOtTime(oo);
					}));
					return true;
				}
				TabloPatch.PatchEntry.Core.ManualEditSavedMainTime(mm);
				TabloPatch.PatchEntry.Core.ManualEditSavedOtTime(oo);
				return true;
			}
			if (key != "names_set")
			{
				return false;
			}
			string[] pp = (v ?? "").Split(new char[] { '|' });
			if (pp.Length != 4)
			{
				return false;
			}
			// БЛОК 4, F4-06 (Q13): названия из веба проверяются на символы, которых нет
			// в шрифте табло (только лог — оператор веб-панели далеко от этого экрана)
			this.Patch_WarnBoardChars(pp[0], "Команда А (веб)", false);
			this.Patch_WarnBoardChars(pp[1], "Город А (веб)", false);
			this.Patch_WarnBoardChars(pp[2], "Команда Б (веб)", false);
			this.Patch_WarnBoardChars(pp[3], "Город Б (веб)", false);
			if (this.InvokeRequired)
			{
				this.BeginInvoke(new System.Windows.Forms.MethodInvoker(delegate
				{
					this.mtb_Command_A.Text = pp[0];
					this.mtb_TownCommand_A.Text = pp[1];
					this.mtb_Command_B.Text = pp[2];
					this.mtb_TownCommand_B.Text = pp[3];
				}));
				return true;
			}
			this.mtb_Command_A.Text = pp[0];
			this.mtb_TownCommand_A.Text = pp[1];
			this.mtb_Command_B.Text = pp[2];
			this.mtb_TownCommand_B.Text = pp[3];
			return true;
		};
		string lastWebStateErr = "";
	// БЛОК 4, F8-08: счётчик для периодической пробы записи в папку программы (~30 с)
	int probeCounter = 0;
	System.Windows.Forms.Timer webCacheTimer = new System.Windows.Forms.Timer();
	webCacheTimer.Interval = 500;
	webCacheTimer.Tick += delegate
	{
		try
		{
			probeCounter++;
			if (probeCounter >= 60)
			{
				probeCounter = 0;
				this.Patch_ProbeWriteAccess();
			}
			// БЛОК 6, З8: статус-строка обновляется, когда меняется число устройств,
			// подключённых к веб-интерфейсу (таймер уже идёт в UI-потоке).
			try
			{
				int regNow = 0;
				int supNow = 0;
				TabloPatch.WebServer.GetSessionCounts(out regNow, out supNow);
				if (regNow != this._patchWebCntReg || supNow != this._patchWebCntSup)
				{
					this._patchWebCntReg = regNow;
					this._patchWebCntSup = supNow;
					this.statusShow();
				}
			}
			catch
			{
			}
			string pub;
			string st = this.Patch_WebState(out pub);
			if (!string.IsNullOrEmpty(st) && st[0] == '{' && st[st.Length - 1] == '}')
			{
				TabloPatch.WebServer.StateCache = st;
			}
			// БЛОК 3, Q15: публичный урезанный state для страницы /display (без пароля)
			if (!string.IsNullOrEmpty(pub) && pub[0] == '{' && pub[pub.Length - 1] == '}')
			{
				TabloPatch.WebServer.PubCache = pub;
			}
			else if (lastWebStateErr != "badjson")
			{
				lastWebStateErr = "badjson";
				TabloPatch.SerialPortLog.Write("WEB: state.json отклонён как не-JSON, кэш не обновлён");
			}
		}
		catch (Exception wex)
		{
			if (wex.Message != lastWebStateErr)
			{
				lastWebStateErr = wex.Message;
				TabloPatch.SerialPortLog.Write("WEB: ОШИБКА формирования state.json, кэш не обновлён: " + wex.Message);
			}
		}
	};
	webCacheTimer.Start();
	// БЛОК 4, F9-05/F5-06: кэш динамических контролов + явные границы числовых полей
	this.Patch_CacheDynamicControls();
	// БЛОК 4, F4-06 (Q13): предупреждение о символах, которых нет в шрифте табло —
	// при ПОТЕРЕ ФОКУСА полем (не на каждое нажатие клавиши). Текст не заменяется.
	EventHandler patchCharsCheck = delegate(object sC, EventArgs eC)
	{
		this.Patch_WarnBoardChars(this.mtb_Command_A.Text, "Команда А", true);
		this.Patch_WarnBoardChars(this.mtb_TownCommand_A.Text, "Город А", true);
		this.Patch_WarnBoardChars(this.mtb_Command_B.Text, "Команда Б", true);
		this.Patch_WarnBoardChars(this.mtb_TownCommand_B.Text, "Город Б", true);
	};
	this.mtb_Command_A.Leave += patchCharsCheck;
	this.mtb_TownCommand_A.Leave += patchCharsCheck;
	this.mtb_Command_B.Leave += patchCharsCheck;
	this.mtb_TownCommand_B.Leave += patchCharsCheck;
		this.Patch_SyncGuardFromSettings();
		this.KeyPreview = true;
		this.statusShow();
		// БЛОК 2, F6-04: автосохранение состояния матча раз в 2 с (только при изменениях)
		this.Patch_StartStateAutosave();
		// БЛОК 2, F6-04: после сбоя/обрыва питания предлагаем вернуть счёт/период/время/удаления
		this.Patch_OfferRestoreState();
	}

		// Token: 0x06000036 RID: 54 RVA: 0x00005F64 File Offset: 0x00004164
		private void Init_gameVars()
		{
			if (this._settingForm != null)
			{
				this._gameTime = TimeSpan.Zero;
				int num = 1;
				try
				{
					num = (int)this.nUD_Period.Value;
				}
				catch
				{
				}
				bool flag = object.Equals(this.gB_Tablo.Tag, "otmanual");
				if (num >= 4)
				{
					if (!flag)
					{
						// БЛОК 4, F9-10/F8-07: единая точка чтения овертайма (кэш + честный лог)
						int otSec = this.Patch_OvertimeSecFromFiles();
						TimeSpan timeSpan = (otSec > 0) ? TimeSpan.FromSeconds((double)otSec) : TimeSpan.FromMinutes(5.0);
						this._gameTotalTime = ((timeSpan.TotalSeconds > 0.0) ? timeSpan : this._settingForm.cfg.GameTime);
					}
				}
				else
				{
					this.gB_Tablo.Tag = null;
					this._gameTotalTime = this._settingForm.cfg.GameTime;
				}
				this._gameStroke = this._settingForm.cfg.ForwardStrokeGame;
				return;
			}
			this._gameTime = TimeSpan.Zero;
			this._gameTotalTime = this._gameTime;
			this._gameStroke = true;
		}

		// Token: 0x06000037 RID: 55 RVA: 0x000060C8 File Offset: 0x000042C8
		private void Init_timeoutVars()
		{
			if (this._settingForm != null)
			{
				this._timeoutTime = TimeSpan.Zero;
				this._timeoutTotalTime = this._settingForm.cfg.TimeoutTime;
				this._timeoutStroke = false;
				this._timeoutVideoEnabled = this._settingForm.cfg.TimeoutVideoEnable;
				return;
			}
			this._timeoutTime = TimeSpan.Zero;
			this._timeoutTotalTime = this._timeoutTime;
			this._timeoutStroke = false;
			this._timeoutVideoEnabled = false;
		}

		// Token: 0x06000038 RID: 56 RVA: 0x00006144 File Offset: 0x00004344
		private void Init_breakVars()
		{
			if (this._settingForm != null)
			{
				this._breakTime = TimeSpan.Zero;
				this._breakTotalTime = this._settingForm.cfg.BreakTime;
				this._breakStroke = false;
				this._breakVideoEnabled = this._settingForm.cfg.BreakVideoEnable;
				return;
			}
			this._breakTime = TimeSpan.Zero;
			this._breakTotalTime = this._breakTime;
			this._breakStroke = false;
			this._breakVideoEnabled = false;
		}

		// Token: 0x06000039 RID: 57 RVA: 0x000061C0 File Offset: 0x000043C0
		// БЛОК 3, F5-02: защитная обёртка обработчиков пульта.
		// Библиотека SerialPortControlBoard.dll вызывает события из своего потока опроса и
		// НЕ имеет try/catch: любое исключение в обработчике убивало поток опроса - пульт
		// "глох" до перезапуска программы. Теперь исключение гасится и пишется в лог, опрос
		// пульта продолжает жить. Сами команды обрабатываются ТОЧНО так же, как раньше
		// (те же обработчики) - добавлен только предохранитель. Протокол пульта не изменён.
		private void Patch_SafePult(string name, object sender, CommandEventArgs e, Action<object, CommandEventArgs> handler)
		{
			try
			{
				if (this.IsDisposed || !this.IsHandleCreated)
				{
					return;   // программа закрывается - команду безопасно игнорировать
				}
				// БЛОК 4, F5-09: время последней команды пульта — для индикатора в статус-строке
				this._patchLastPultCmdUtc = DateTime.Now;
				handler(sender, e);
			}
			catch (Exception ex)
			{
				try { TabloPatch.SerialPortLog.Write("ПУЛЬТ " + name + ": ошибка обработки команды (поток опроса спасён): " + ex.Message); } catch { }
			}
		}

		private int _comRetryCount;   // БЛОК 3: счётчик попыток открыть COM (для редкого логирования)

		private void InitialControlBoard()
		{
			// БЛОК 3: замеряем и логируем открытие порта пульта (долгое открытие = признак подвисшего USB-COM)
			int comT0 = Environment.TickCount;
			this._Controlboard = new ControlBoard(this._settingForm.cfg.Port, Convert.ToInt32(this._settingForm.cfg.BaudRate), (Parity)Enum.Parse(typeof(Parity), this._settingForm.cfg.Parity), Convert.ToInt32(this._settingForm.cfg.DataBit), (StopBits)Enum.Parse(typeof(StopBits), this._settingForm.cfg.StopBits));
			int comDt = Environment.TickCount - comT0;
			if (this._Controlboard.SuccessfulPortCreate)
			{
				this._comRetryCount = 0;
				try { TabloPatch.SerialPortLog.Write("COM: пульт - порт " + this._settingForm.cfg.Port + " открыт за " + comDt.ToString() + " мс"); } catch { }
				this._Controlboard.ParentForm = this;
				this._Controlboard.OnCommandResetMatch += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("ResetMatch", sPult, ePult, this._Controlboard_OnCommandResetMatch); };
				this._Controlboard.OnCommandSetBreak += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("SetBreak", sPult, ePult, this._Controlboard_OnCommandSetBreak); };
				this._Controlboard.OnCommandSetCount += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("SetCount", sPult, ePult, this._Controlboard_OnCommandSetCount); };
				this._Controlboard.OnCommandSetCounterGame += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("SetCounterGame", sPult, ePult, this._Controlboard_OnCommandSetCounterGame); };
				this._Controlboard.OnCommandSetName_A += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("SetName_A", sPult, ePult, this._Controlboard_OnCommandSetName_A); };
				this._Controlboard.OnCommandSetName_B += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("SetName_B", sPult, ePult, this._Controlboard_OnCommandSetName_B); };
				this._Controlboard.OnCommandSetScript += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("SetScript", sPult, ePult, this._Controlboard_OnCommandSetScript); };
				this._Controlboard.OnCommandSetTimeout += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("SetTimeout", sPult, ePult, this._Controlboard_OnCommandSetTimeout); };
				this._Controlboard.OnCommandSetTimerGame += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("SetTimerGame", sPult, ePult, this._Controlboard_OnCommandSetTimerGame); };
				this._Controlboard.OnCommandSetTownName_A += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("SetTownName_A", sPult, ePult, this._Controlboard_OnCommandSetTownName_A); };
				this._Controlboard.OnCommandSetTownName_B += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("SetTownName_B", sPult, ePult, this._Controlboard_OnCommandSetTownName_B); };
				this._Controlboard.OnCommandSetWhistle += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("SetWhistle", sPult, ePult, this._Controlboard_OnCommandSetWhistle); };
				this._Controlboard.OnCommandDelGamer += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("DelGamer", sPult, ePult, this._Controlboard_OnCommandDelGamer); };
				this._Controlboard.OnCommandTimeoutCounters += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("TimeoutCounters", sPult, ePult, this._Controlboard_OnCommandTimeoutCounters); };
				this._Controlboard.OnCommandSetBreakMode += delegate(object sPult, CommandEventArgs ePult) { this.Patch_SafePult("SetBreakMode", sPult, ePult, this._Controlboard_OnCommandSetBreakMode); };
				this._StatusFlags = frmMain.statusFlags.portOpen;
			}
			else
			{
				this._Controlboard.Dispose();
				this._Controlboard = null;
				// БЛОК 3: автоповтор каждые 5 с уже был; добавлен внятный лог (редкий, чтобы не засорять)
				try
				{
					this._comRetryCount++;
					if (this._comRetryCount == 1 || this._comRetryCount % 60 == 0)
					{
						TabloPatch.SerialPortLog.Write("COM: пульт - порт " + this._settingForm.cfg.Port + " НЕ открылся (" + comDt.ToString() + " мс, попытка " + this._comRetryCount.ToString() + ") - повтор каждые 5 сек" + ((comDt > 2000) ? "; ВНИМАНИЕ: открытие заняло много времени, возможен подвисший USB-COM" : ""));
					}
				}
				catch
				{
				}
				if (this._ControlboardTimer != null)
				{
					this._ControlboardTimer.Start();
				}
				this._StatusFlags = frmMain.statusFlags.portNotOpen;
			}
			this.statusShow();
		}

		// Token: 0x0600003A RID: 58 RVA: 0x00006414 File Offset: 0x00004614
				private void _promptTimer_Tick(object sender, EventArgs e)
		{
			Multimedia.Timer timer = (Multimedia.Timer)sender;
			if (this.thfPrompt == null)
			{
				return;
			}
			DateTime now = DateTime.Now;
			if (this.gs_Clock != null)
			{
				this.gs_Clock.Text = now.ToString("HH:mm:ss");
			}
			string text = DateTime.Now.ToString("dd MMMM") + " /" + DateTime.Now.ToString("ddd", new CultureInfo("ru-RU")) + "/";
			if (this.gs_Date != null)
			{
				this.gs_Date.Text = text;
			}
			if (this._beforeTimerEnabled)
			{
				TimeSpan timeSpan = TimeSpan.FromTicks(DateTime.Now.Ticks);
				// БЛОК 4, F6-16: база предматчевого отсчёта — отдельное поле _patchBeforeRef
				// (раньше _storedTime перезаписывался стартом матча, и отсчёт заставки сбивался)
				int sec = (int)Math.Floor(timeSpan.TotalSeconds - Math.Floor(this._patchBeforeRef.TotalSeconds));
				if (sec < 0)
				{
					sec = 0;
				}
				if ((double)sec != this._beforeTime.TotalSeconds)
				{
					timer.Period = 1000;
					this._beforeTime = TimeSpan.FromSeconds((double)sec);
					if (this._beforeTime >= this._beforeTotalTime)
					{
						timer.Stop();
						this._beforeTime = this._beforeTotalTime;
						this.EndBefore_OnTimer();
					}
					if (this.gs_BeforeGameTime != null)
					{
						this.TabloMode();
						TimeSpan t = this._settingForm.cfg.ForwardStrokeBeforeStart ? this._beforeTime : (this._beforeTotalTime - this._beforeTime);
						this.gs_BeforeGameTime.EffectIndex = Effects.Stop;
						if (t == this._beforeTime_2 || t == this._beforeTime_3)
						{
							this.BeforeStartPlayer();
						}
					}
				}
			}
		}
		// Token: 0x0600003B RID: 59 RVA: 0x00006590 File Offset: 0x00004790
		private void _timerRestore_Tick(object sender, EventArgs e)
		{
			((Multimedia.Timer)sender).Stop();
			this.Cmd_ScriptStop();
			int num = (int)this.nUD_Period.Value;
			int num2 = (this.nUD_Period.Tag is int) ? ((int)this.nUD_Period.Tag) : num;
			if (num != num2)
			{
								this.Patch_PeriodEndRestore();
				this.nUD_Period.Tag = num;
				bool flag = object.Equals(this.gB_Tablo.Tag, "otmanual");
				if (num >= 4 && !flag)
				{
					// БЛОК 4, F9-10: единая точка чтения овертайма
					TimeSpan gameTotalTime = TimeSpan.FromSeconds((double)this.Patch_OvertimeSecFromFiles());
					if (gameTotalTime.TotalSeconds > 0.0)
					{
						this._gameTotalTime = gameTotalTime;
					}
				}
				else if (num < 4 && this._settingForm != null)
				{
					this.gB_Tablo.Tag = null;
					this._gameTotalTime = this._settingForm.cfg.GameTime;
					if (this._gameTotalTime.TotalSeconds < 1.0)
					{
						this._gameTotalTime = TimeSpan.FromMinutes(1.0);
					}
				}
				this.TabloMode();
				return;
			}
			this.nUD_Period.Tag = num;
		}

		// Token: 0x0600003C RID: 60 RVA: 0x00006724 File Offset: 0x00004924
		private void _timerGame_Tick(object sender, EventArgs e)
		{
			Multimedia.Timer timer = (Multimedia.Timer)sender;
			double num = 1.0;
			// БЛОК 4, F9-05: вместо поиска контрола через Controls.Find КАЖДЫЕ 10 мс
			// (обход всего дерева контролов 100 раз/с) берём ссылку из кэша.
			ComboBox comboBox = this.Patch_GetCbSpeed();
			if (comboBox != null && comboBox.SelectedItem != null)
			{
				try
				{
					num = double.Parse(comboBox.SelectedItem.ToString(), CultureInfo.InvariantCulture);
				}
				catch
				{
					num = 1.0;
				}
			}
			// БЛОК 2, F2-01: время берётся из Environment.TickCount (реальное), а не
			// накапливается номиналом тика — фризы UI больше не "съедают" секунды матча.
			if (num != this._patchSpeedLast)
			{
				this._patchSpeedLast = num;   // смена скорости на лету: фиксируем базу, скачка нет
				this.Patch_GameTimeMarkStart();
			}
			this.Patch_GameTimeRefresh(num);
			bool flag = false;
			if (this._gameTime >= this._gameTotalTime)
			{
				flag = true;
				timer.Stop();
				this._gameTime = this._gameTotalTime;
				// БЛОК 2, F6-03: Patch_PeriodEndRestore() УБРАН из 10-мс тика — теперь он
				// вызывается явно в EndGame_OnTimer (раньше единственный авто-вызов был
				// привязан к концу бегущей строки и в штатном сценарии не срабатывал).
				// Запись config.xml в тике больше не выполняется.
				this.EndGame_OnTimer();
			}
			int num2 = (int)Math.Ceiling(this._gameTime.TotalSeconds);
			int num3 = 0;
			if (comboBox != null && comboBox.Tag is int)
			{
				num3 = (int)comboBox.Tag;
			}
			int num4 = (num3 == 0) ? 0 : (num2 - num3);
						int dec = num4;
			if (dec == 0 && flag)
			{
				dec = 1;
			}
			if (comboBox != null)
			{
				comboBox.Tag = num2;
			}
			try
			{
			if (dec > 0)
			{
				this._gameShowOtherString = true;
				for (int i = this.lB_DelPeople_A.Items.Count - 1; i >= 0; i--)
				{
					string text = (string)this.lB_DelPeople_A.Items[i];
					int num5 = (int)TimeSpan.Parse("00:" + text.Substring(3, 5)).TotalSeconds - dec;
					if (num5 <= 0)
					{
						if (flag)
						{
							this.lB_DelPeople_A.Items[i] = text.Substring(0, 3) + "00:00";
						}
						else
						{
							this.lB_DelPeople_A.Items.RemoveAt(i);
						}
					}
					else
					{
						ListBox.ObjectCollection items = this.lB_DelPeople_A.Items;
						int num6 = i;
						string text2 = text.Substring(0, 3);
						int j = TimeSpan.FromSeconds((double)num5).Minutes;
						string text3 = j.ToString("D2");
						string text4 = ":";
						int num7 = num6;
						string text5 = text2;
						string text6 = text3;
						string text7 = text4;
						int index = num7;
						string str = text5;
						string str2 = text6;
						string str3 = text7;
						j = TimeSpan.FromSeconds((double)num5).Seconds;
						items[index] = str + str2 + str3 + j.ToString("D2");
					}
				}
				for (int k = this.lB_DelPeople_B.Items.Count - 1; k >= 0; k--)
				{
					string text8 = (string)this.lB_DelPeople_B.Items[k];
					int num8 = (int)TimeSpan.Parse("00:" + text8.Substring(3, 5)).TotalSeconds - dec;
					if (num8 <= 0)
					{
						if (flag)
						{
							this.lB_DelPeople_B.Items[k] = text8.Substring(0, 3) + "00:00";
						}
						else
						{
							this.lB_DelPeople_B.Items.RemoveAt(k);
						}
					}
					else
					{
						ListBox.ObjectCollection items2 = this.lB_DelPeople_B.Items;
						int num9 = k;
						string text9 = text8.Substring(0, 3);
						int j = TimeSpan.FromSeconds((double)num8).Minutes;
						string text10 = j.ToString("D2");
						string text11 = ":";
						int num10 = num9;
						string text12 = text9;
						string text13 = text10;
						string text14 = text11;
						int index2 = num10;
						string str4 = text12;
						string str5 = text13;
						string str6 = text14;
						j = TimeSpan.FromSeconds((double)num8).Seconds;
						items2[index2] = str4 + str5 + str6 + j.ToString("D2");
					}
				}
					}
			}
			catch
			{
			}
			this.TabloMode();
			TimeSpan t = this._gameTotalTime - this._gameTime;
			if (t < TimeSpan.Zero)
			{
				t = TimeSpan.Zero;
			}
			int num11 = 1;
			try
			{
				num11 = (int)this.nUD_Period.Value;
			}
			catch
			{
			}
			bool flag2 = false;
			if (num11 == 3)
			{
				flag2 = (t.TotalMinutes <= 2.0);
			}
			else if (num11 < 3)
			{
				flag2 = (t.TotalMinutes <= 1.0);
			}
						TimeSpan t2 = this._gameTime;
			if (!this._gameStroke)
			{
				t2 = this._gameTotalTime - t2;
				if (!flag2 && t2.Milliseconds > 0)
				{
					t2 = TimeSpan.FromSeconds(Math.Ceiling(t2.TotalSeconds));
				}
				if (flag2)
				{
					double rem = t2.TotalSeconds;
					int whole = (int)Math.Ceiling(rem);
					int hundredths = 99 - (int)((whole - rem) * 100);
					if (hundredths < 0)
					{
						hundredths = 0;
					}
					if (hundredths > 99)
					{
						hundredths = 99;
					}
								if (whole <= 0)
			{
				hundredths = 0;
			}
					t2 = TimeSpan.FromSeconds((double)whole) + TimeSpan.FromMilliseconds((double)(hundredths * 10));
				}
			}
			string text16;
			if (!flag2)
			{
				int j = t2.Minutes;
				string str7 = j.ToString("D2");
				string text15 = ":";
				string str8 = text15;
				j = t2.Seconds;
				text16 = str7 + str8 + j.ToString("D2");
			}
			else
			{
				string[] array2 = new string[5];
				int num12 = 0;
				int num13 = num12;
				int j = t2.Minutes;
				array2[num13] = j.ToString("D2");
				array2[1] = ":";
				int num14 = 2;
				int num15 = num14;
				j = t2.Seconds;
				array2[num15] = j.ToString("D2");
				array2[3] = ":";
				int num16 = 4;
				int num17 = num16;
				j = t2.Milliseconds;
				array2[num17] = j.ToString("D3").Substring(0, 2);
				text16 = string.Concat(array2);
			}
			string text17 = text16;
			// БЛОК 4, F9-05: lSpeed тоже из кэша (искался каждый тик)
			Control lSpeedCtl = this.Patch_GetLSpeed();
			string text18 = this.gs_LWindow_1.Text;
			string text19 = this.gs_RWindow_1.Text;
			string text20 = num11.ToString("D2");
			string text21 = num11.ToString();
			// БЛОК 4, F9-04: StringBuilder вместо склейки строк в 10-мс тике
			System.Text.StringBuilder sbPenA = new System.Text.StringBuilder();
			foreach (object obj in this.lB_DelPeople_A.Items)
			{
				if (sbPenA.Length > 0)
				{
					sbPenA.Append("; ");
				}
				sbPenA.Append((string)obj);
			}
			string text22 = sbPenA.ToString();
			System.Text.StringBuilder sbPenB = new System.Text.StringBuilder();
			foreach (object obj2 in this.lB_DelPeople_B.Items)
			{
				if (sbPenB.Length > 0)
				{
					sbPenB.Append("; ");
				}
				sbPenB.Append((string)obj2);
			}
			string text23 = sbPenB.ToString();
			string text24 = string.Concat(new string[]
			{
				text17,
				"|",
				text18,
				"|",
				text19,
				"|",
				text20,
				"|",
				text22,
				"|",
				text23
			});
			object obj3 = (lSpeedCtl != null) ? lSpeedCtl.Tag : null;
			string text25 = null;
			int num18 = 0;
			if (obj3 is object[])
			{
				object[] array4 = (object[])obj3;
				text25 = (array4[0] as string);
				num18 = (int)array4[1];
			}
			bool flag4;
			bool flag3 = flag4 = (text24 != text25);
			if (flag3 && text25 != null)
			{
				int num19 = text24.IndexOf('|');
				int num20 = text25.IndexOf('|');
				flag4 = (num19 < 0 || num20 < 0 || text24.Substring(num19) != text25.Substring(num20));
			}
			int tickCount = Environment.TickCount;
			if (flag3 && (flag4 || tickCount - num18 >= 250))
			{
				if (lSpeedCtl != null)
				{
					lSpeedCtl.Tag = new object[]
					{
						text24,
						tickCount
					};
				}
				// БЛОК 4, F2-03: tablo_stream.txt перечитывается не чаще раза в 2 с (кэш),
				// а не при каждом изменении картинки. vMix-интеграция не изменена (Q22a):
				// те же поля, тот же порядок отдачи — просто убран лишний дисковый ввод.
				this.Patch_LoadStreamCfg();
				string a = this._patchStreamVmixApi;
				string text26 = this._patchStreamVmixHost;
				string stringToEscape = this._patchStreamVmixInput;
				string text27 = this._patchStreamVmixField;
				string a2 = this._patchStreamUdp;
				string text28 = this._patchStreamUdpHost;
				string a3 = this._patchStreamFile;
				if (a == "1" && text26.Length > 0)
				{
					foreach (string[] array7 in new string[][]
					{
						new string[]
						{
							text27,
							text17
						},
						new string[]
						{
							"Период.Text",
							text21
						},
						new string[]
						{
							"Счет голов хозяев.Text",
							text18
						},
						new string[]
						{
							"Счет голов гостей.Text",
							text19
						},
						new string[]
						{
							"Удаление хозяев.Text",
							text22
						},
						new string[]
						{
							"Удаление гостей.Text",
							text23
						}
					})
					{
						try
						{
														(this._vmixClient ?? (this._vmixClient = new WebClient())).DownloadStringAsync(new Uri(string.Concat(new string[]
							{
								"http://",
								text26,
								"/API/?Function=SetText&Input=",
								Uri.EscapeDataString(stringToEscape),
								"&SelectedName=",
								Uri.EscapeDataString(array7[0]),
								"&Value=",
								Uri.EscapeDataString(array7[1])
							})));
						}
						catch
						{
						}
					}
				}
				string text31 = string.Concat(new string[]
				{
					"[{\"time\":\"",
					text17,
					"\",\"period\":\"",
					text21,
					"\",\"score_home\":\"",
					text18,
					"\",\"score_away\":\"",
					text19,
					"\",\"pen_home\":\"",
					text22,
					"\",\"pen_away\":\"",
					text23,
					"\"}]"
				});
				if (a2 == "1" && text28.Length > 0)
				{
					try
					{
						string[] array8 = text28.Split(new char[]
						{
							':'
						});
						UdpClient udpClient = this.but_TimeReset.Tag as UdpClient;
						if (udpClient == null)
						{
							udpClient = new UdpClient();
							this.but_TimeReset.Tag = udpClient;
						}
						byte[] bytes = Encoding.UTF8.GetBytes(text31);
						udpClient.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(array8[0]), int.Parse(array8[1])));
					}
					catch
					{
					}
				}
				if (a3 == "1")
				{
					// БЛОК 4, F8-09: «быстрая атомарная» запись (tmp -> File.Replace):
					// внешние потребители больше не увидят полузаписанный файл.
					// Ошибки не проглатываются молча — считаются в диагностике (F8-03).
					try
					{
						Patch_WriteFileQuickAtomic(Path.Combine(Application.StartupPath, "tablo_time.txt"), text17);
					}
					catch (Exception exF)
					{
						TabloPatch.Diag.Note("Запись tablo_time.txt", exF);
					}
					try
					{
						Patch_WriteFileQuickAtomic(Path.Combine(Application.StartupPath, "tablo_full.json"), text31);
					}
					catch (Exception exF2)
					{
						TabloPatch.Diag.Note("Запись tablo_full.json", exF2);
					}
				}
			}
		}

		// Token: 0x0600003D RID: 61 RVA: 0x00007254 File Offset: 0x00005454
		private void _timerTimeOut_Tick(object sender, EventArgs e)
		{
			Multimedia.Timer timer = (Multimedia.Timer)sender;
			this._timeoutTime += TimeSpan.FromSeconds(1.0);
			if (this._timeoutTime >= this._timeoutTotalTime)
			{
				timer.Stop();
				this._timeoutTime = this._timeoutTotalTime;
				this.EndTimeout_OnTimer();
			}
			if (this._timeoutVideoEnabled && this._videoEnabled)
			{
				this.Cmd_ShowVideo();
			}
			this.TabloMode();
		}

		// Token: 0x0600003E RID: 62 RVA: 0x000072D0 File Offset: 0x000054D0
		private void _timerBreak_Tick(object sender, EventArgs e)
		{
			Multimedia.Timer timer = (Multimedia.Timer)sender;
			this._breakTime += TimeSpan.FromSeconds(1.0);
			if (this._breakTime >= this._breakTotalTime)
			{
				timer.Stop();
				this._breakTime = this._breakTotalTime;
				this.EndBreak_OnTimer();
			}
			if (this._breakVideoEnabled && this._videoEnabled)
			{
				this.Cmd_ShowVideo();
			}
			this.TabloMode();
		}

		// Token: 0x0600003F RID: 63 RVA: 0x0000734C File Offset: 0x0000554C
		private void _ControlboardTimer_Tick(object sender, EventArgs e)
		{
			System.Windows.Forms.Timer timer = (System.Windows.Forms.Timer)sender;
			timer.Stop();
			this.InitialControlBoard();
		}

		// Token: 0x06000040 RID: 64 RVA: 0x0000736C File Offset: 0x0000556C
		private void TabloMode()
		{
			// БЛОК 4, F9-08: блокировка lockTablo удалена — TabloMode ВСЕГДА вызывается
			// в UI-потоке (таймеры с SynchronizingObject, веб/пульт через BeginInvoke),
			// конкуренции за него нет. Скобки блока сохранены — логика не тронута.
			{
				if (this._TabloMode == frmMain.tabloMode.game)
				{
					if (this.gs_CentreWindow != null)
					{
						if (this.thf == null)
						{
							this.OpenTabloHockey();
						}
						if (this._GameMode == frmMain.gameMode.GameStarted)
						{
							if (this.gs_CentreWindow.TwoLines)
							{
								this.gs_CentreWindow.TwoLines = false;
								this.gs_CentreWindow.Text_2 = string.Empty;
							}
							if ((int)this.nUD_Period.Value < 4 && this._gameTotalTime - this._gameTime <= TimeSpan.FromMinutes(((int)this.nUD_Period.Value == 3) ? 2.0 : 1.0))
							{
								this.SwitchShow_gs_CentreWindow(false);
								this.gs_CentreWindow.Text = this.Show_gs_CentreWindow(this._gameTime, this._gameTotalTime, this._gameStroke, true);
							}
							else
							{
								this.SwitchShow_gs_CentreWindow(true);
								this.gs_CentreWindow.Text = this.Show_gs_CentreWindow(this._gameTime, this._gameTotalTime, this._gameStroke, false);
							}
							if (this._gameShowOtherString)
							{
								this._gameShowOtherString = false;
								this.ShowGameTimer();
								this.ShowPenalty(0, this.lB_DelPeople_A, this.gs_LWindow_2);
								this.ShowPenalty(1, this.lB_DelPeople_A, this.gs_LWindow_3);
								this.ShowPenalty(0, this.lB_DelPeople_B, this.gs_RWindow_2);
								this.ShowPenalty(1, this.lB_DelPeople_B, this.gs_RWindow_3);
							}
							this.but_BeforeStart.Enabled = false;
							this.but_ShowPrompt.Enabled = false;
							this.but_ScriptPlay.Enabled = true;
							this.but_StartGame.Enabled = false;
							this.but_StopGame.Enabled = true;
							this.but_Timeout.Enabled = false;
							this.but_Beep.Enabled = true;
							this.but_BreakGame.Enabled = false;
							this.but_ResetGame.Enabled = false;
							this.but_TimeReset.Enabled = false;
							this.gB_GameTime.Enabled = false;
						}
						else if (this._GameMode == frmMain.gameMode.GameStoped)
						{
							if (this.gs_CentreWindow.TwoLines)
							{
								this.gs_CentreWindow.TwoLines = false;
								this.gs_CentreWindow.Text_2 = string.Empty;
							}
							if (!this._gameOver)
							{
								if ((int)this.nUD_Period.Value < 4 && this._gameTotalTime - this._gameTime <= TimeSpan.FromMinutes(((int)this.nUD_Period.Value == 3) ? 2.0 : 1.0))
								{
									this.SwitchShow_gs_CentreWindow(false);
									this.gs_CentreWindow.Text = this.Show_gs_CentreWindow(this._gameTime, this._gameTotalTime, this._gameStroke, true);
								}
								else
								{
									this.SwitchShow_gs_CentreWindow(true);
									this.gs_CentreWindow.Text = this.Show_gs_CentreWindow(this._gameTime, this._gameTotalTime, this._gameStroke, false);
								}
								this.but_BeforeStart.Enabled = false;
								this.but_ShowPrompt.Enabled = true;
								this.but_ScriptPlay.Enabled = true;
								this.but_StartGame.Enabled = true;
								this.but_StopGame.Enabled = false;
								this.but_Timeout.Enabled = true;
								this.but_Beep.Enabled = true;
								this.but_BreakGame.Enabled = false;
								this.but_ResetGame.Enabled = true;
								this.but_TimeReset.Enabled = true;
								this.gB_GameTime.Enabled = true;
							}
							else
							{
								// БЛОК 4, F2-02b: переключение геометрии (thf.Stop/Start) выполняется
								// ОДИН раз в момент смены формата, а не каждые 10 мс.
								bool patchWantWide = this.gs_CentreWindow.Text.Length > 5;
								if (patchWantWide && this._patchCentreWideApplied != 1)
								{
									this._patchCentreWideApplied = 1;
									if (this.thf != null)
									{
										this.thf.Stop();
									}
									this.gs_CentreWindow.Height = 32;
									this.gs_CentreWindow.Length = 104;
									this.gs_CentreWindow.Left = 68;
									this.gs_CentreWindow.Top = 52;
									if (this.thf != null)
									{
										this.thf.Start();
									}
								}
								else if (!patchWantWide && this._patchCentreWideApplied != 0)
								{
									this._patchCentreWideApplied = 0;
								}
								this.gs_CentreWindow.Text = this.Show_gs_CentreWindow(this._gameTime, this._gameTotalTime, this._gameStroke, false);
								// БЛОК 4, F2-02b: ShowPenalty x4 — только когда списки удалений изменились
								this.Patch_ShowPenaltiesOnChanged();
								this.but_BeforeStart.Enabled = false;
								this.but_ShowPrompt.Enabled = true;
								this.but_ScriptPlay.Enabled = true;
								this.but_StartGame.Enabled = true;
								this.but_StopGame.Enabled = false;
								this.but_Timeout.Enabled = false;
								this.but_Beep.Enabled = true;
								this.but_BreakGame.Enabled = true;
								this.but_ResetGame.Enabled = true;
								this.but_TimeReset.Enabled = true;
								this.gB_GameTime.Enabled = true;
							}
						}
						else if (this._GameMode == frmMain.gameMode.Timeout)
						{
							this.SwitchShow_gs_CentreWindow(true);
							if (!this.gs_CentreWindow.TwoLines)
							{
								this.gs_CentreWindow.TwoLines = true;
							}
							this.gs_CentreWindow.Text = this.Show_gs_CentreWindow(this._gameTime, this._gameTotalTime, this._gameStroke, false);
							this.gs_CentreWindow.Text_2 = this.Show_gs_CentreWindow(this._timeoutTime, this._timeoutTotalTime, this._timeoutStroke, false);
							this.ShowPenalty(0, this.lB_DelPeople_A, this.gs_LWindow_2);
					this.ShowPenalty(1, this.lB_DelPeople_A, this.gs_LWindow_3);
					this.ShowPenalty(0, this.lB_DelPeople_B, this.gs_RWindow_2);
					this.ShowPenalty(1, this.lB_DelPeople_B, this.gs_RWindow_3);
							this.but_BeforeStart.Enabled = false;
							this.but_ShowPrompt.Enabled = false;
							this.but_ScriptPlay.Enabled = true;
							this.but_StartGame.Enabled = true;
							this.but_StopGame.Enabled = true;
							this.but_Timeout.Enabled = false;
							this.but_Beep.Enabled = true;
							this.but_BreakGame.Enabled = false;
							this.but_ResetGame.Enabled = false;
							this.but_TimeReset.Enabled = false;
							this.gB_GameTime.Enabled = false;
						}
						else if (this._GameMode == frmMain.gameMode.Break)
						{
							this.SwitchShow_gs_CentreWindow(true);
							this.gs_CentreWindow.Text = this.Show_gs_CentreWindow(this._breakTime, this._breakTotalTime, this._breakStroke, false);
					this.ShowPenalty(0, this.lB_DelPeople_A, this.gs_LWindow_2);
					this.ShowPenalty(1, this.lB_DelPeople_A, this.gs_LWindow_3);
					this.ShowPenalty(0, this.lB_DelPeople_B, this.gs_RWindow_2);
					this.ShowPenalty(1, this.lB_DelPeople_B, this.gs_RWindow_3);
							this.but_BeforeStart.Enabled = false;
							this.but_ShowPrompt.Enabled = false;
							this.but_ScriptPlay.Enabled = true;
							this.but_StartGame.Enabled = true;
							this.but_StopGame.Enabled = true;
							this.but_Timeout.Enabled = false;
							this.but_Beep.Enabled = true;
							this.but_BreakGame.Enabled = false;
							this.but_ResetGame.Enabled = false;
							this.but_TimeReset.Enabled = false;
							this.gB_GameTime.Enabled = false;
						}
						if (this.gs_Main != null && !this.gs_Main.StartScript && !this.gs_Main.ShowTablo4String)
						{
							this.gs_Main.ShowTablo4String = true;
						}
						this.but_ShowPrompt.Text = "Показать заставку";
					}
				}
				else if (this._TabloMode == frmMain.tabloMode.prompt)
				{
					if (this.thfPrompt == null)
					{
						this.OpenPromptHockey();
					}
					if (this.gs_BeforeGameTime != null)
					{
						if (this._beforeTimerEnabled)
						{
													TimeSpan timeSpan = this._settingForm.cfg.ForwardStrokeBeforeStart ? this._beforeTime : (this._beforeTotalTime - this._beforeTime);
							this.gs_BeforeGameTime.Text = FmtClock(timeSpan, false);
						}
						else if (this.gs_BeforeGameTime.Text != string.Empty)
						{
							this.gs_BeforeGameTime.Text = string.Empty;
						}
					}
					if (this._beforeTimerEnabled)
					{
						this.but_ShowPrompt.Enabled = false;
					}
					else
					{
						this.but_ShowPrompt.Enabled = true;
					}
					this.but_BeforeStart.Enabled = true;
					this.but_ShowPrompt.Enabled = true;
					this.but_ScriptPlay.Enabled = false;
					this.but_StartGame.Enabled = true;
					this.but_StopGame.Enabled = true;
					this.but_Timeout.Enabled = false;
					this.but_Beep.Enabled = true;
					this.but_BreakGame.Enabled = false;
					this.but_ResetGame.Enabled = false;
					this.but_TimeReset.Enabled = false;
					this.gB_GameTime.Enabled = false;
					this.but_ShowPrompt.Text = "Показать табло";
				}
				else if (this._TabloMode == frmMain.tabloMode.video && this.vpf == null)
				{
					if (this._GameMode == frmMain.gameMode.Break)
					{
						this.OpenVideo(this.cLB_VideoFiles);
					}
					else if (this._GameMode == frmMain.gameMode.Timeout)
					{
						this.OpenVideo(this.cLB_TimeoutVideoFiles);
					}
				}
			}
		}

		// Token: 0x06000041 RID: 65 RVA: 0x00007C10 File Offset: 0x00005E10
		private void SwitchShow_gs_CentreWindow(bool wantShort)
		{
			int num = wantShort ? 32 : 24;
			if (this.gs_CentreWindow.Height == num)
			{
				return;
			}
			if (this.thf != null)
			{
				this.thf.Stop();
			}
			if (wantShort)
			{
				this.gs_CentreWindow.Height = 32;
				this.gs_CentreWindow.Length = 104;
				this.gs_CentreWindow.Left = 68;
				this.gs_CentreWindow.Top = 52;
			}
			else
			{
				this.gs_CentreWindow.Height = 24;
				this.gs_CentreWindow.Length = 120;
				this.gs_CentreWindow.Left = 60;
				this.gs_CentreWindow.Top = 56;
			}
			if (this.thf != null)
			{
				this.thf.Start();
			}
		}

		// Token: 0x06000042 RID: 66 RVA: 0x00007CCC File Offset: 0x00005ECC
		private string Show_gs_CentreWindow(TimeSpan time, TimeSpan total, bool stroke, bool flag)
		{
			TimeSpan t = total - time;
			if (t < TimeSpan.Zero)
			{
				t = TimeSpan.Zero;
			}
			bool flag2 = time == this._gameTime && total == this._gameTotalTime;
			int num = 1;
			try
			{
				num = (int)this.nUD_Period.Value;
			}
			catch
			{
			}
			bool flag3 = false;
			if (flag2)
			{
				if (num == 3)
				{
					flag3 = (t.TotalMinutes <= 2.0);
				}
				else if (num < 3)
				{
					flag3 = (t.TotalMinutes <= 1.0);
				}
			}
			if (flag2 && this._GameMode != frmMain.gameMode.Timeout && this._GameMode != frmMain.gameMode.Break)
			{
				this.SwitchShow_gs_CentreWindow(!flag3);
			}
						TimeSpan t2 = time;
			if (!stroke)
			{
				t2 = total - t2;
				if (!flag3 && t2.Milliseconds > 0)
				{
					t2 = TimeSpan.FromSeconds(Math.Ceiling(t2.TotalSeconds));
				}
				if (flag3)
				{
					double rem = t2.TotalSeconds;
					int whole = (int)Math.Ceiling(rem);
					int hundredths = 99 - (int)((whole - rem) * 100);
					if (hundredths < 0)
					{
						hundredths = 0;
					}
					if (hundredths > 99)
					{
						hundredths = 99;
					}
						if (whole <= 0)
					{
						hundredths = 0;
					}
										if (whole > 0 && rem == (double)whole && this._GameMode != frmMain.gameMode.GameStarted)
					{
						hundredths = 0;
					}
					t2 = TimeSpan.FromSeconds((double)whole) + TimeSpan.FromMilliseconds((double)(hundredths * 10));
				}
			}
			if (flag3)
			{
				return FmtClock(t2, true);
			}
			return FmtClock(t2, false);
		}

		// Token: 0x06000043 RID: 67 RVA: 0x00007E68 File Offset: 0x00006068
		private void ShowGameTimer()
		{
			try
			{
				TimeSpan timeSpan;
				if (this._settingForm != null && this._settingForm.cfg.ForwardStrokeGame)
				{
					timeSpan = this._gameTime;
				}
				else
				{
					timeSpan = this._gameTotalTime - this._gameTime;
				}
				if (timeSpan < TimeSpan.Zero)
				{
					timeSpan = TimeSpan.Zero;
				}
				DateTime dateTime = this.dTP_GameTime.MinDate + timeSpan;
				if (dateTime < this.dTP_GameTime.MinDate)
				{
					dateTime = this.dTP_GameTime.MinDate;
				}
				if (dateTime > this.dTP_GameTime.MaxDate)
				{
					dateTime = this.dTP_GameTime.MaxDate;
				}
				this.dTP_GameTime.Value = dateTime;
			}
			catch
			{
			}
		}

		// Token: 0x06000045 RID: 69 RVA: 0x00007FB0 File Offset: 0x000061B0
		private void ShowPenalty(int index, ListBox lb, GraphString gs)
		{
			try
			{
				if (gs != null)
				{
					if (lb != null && index >= 0 && index < lb.Items.Count)
					{
						string text = (string)lb.Items[index];
						int num = 0;
						if (text != null && text.Length >= 8)
						{
							num = int.Parse(text.Substring(3, 2)) * 60 + int.Parse(text.Substring(6, 2));
						}
						gs.Text = text;
												gs.EffectIndex = Effects.Stop;
					}
					else
					{
						gs.EffectIndex = Effects.Stop;
						gs.Text = "";
					}
				}
			}
			catch
			{
			}
		}

		// Token: 0x06000046 RID: 70 RVA: 0x00008080 File Offset: 0x00006280
		private void LoadVideoList(string VideoListPath)
		{
			try
			{
				string[] source = File.ReadAllLines(VideoListPath);
				this.cLB_VideoFiles.Items.Clear();
				IEnumerable<string> enumerable = source.TakeWhile((string s, int sb) => s != "--TimeoutVideoFiles--");
				enumerable = enumerable.Skip(1);
				foreach (string item in enumerable)
				{
					this.cLB_VideoFiles.Items.Add(item);
				}
				enumerable = source.SkipWhile((string s, int sb) => s != "--TimeoutVideoFiles--");
				enumerable = enumerable.Skip(1);
				foreach (string item2 in enumerable)
				{
					this.cLB_TimeoutVideoFiles.Items.Add(item2);
				}
				this._settingForm.cfg.MediaPlaylistPath = VideoListPath;
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Ошибка загрузки списка видео файлов");
			}
		}

		// Token: 0x06000047 RID: 71 RVA: 0x000022F0 File Offset: 0x000004F0
		public frmMain()
		{
			this.InitializeComponent();
		}

		// Token: 0x06000048 RID: 72 RVA: 0x000081C4 File Offset: 0x000063C4
		private void frmMain_Load(object sender, EventArgs e)
		{
			Control.CheckForIllegalCrossThreadCalls = false;
			this.Text = "Табло by IY";
			Patch_CheckRuntime();
			 		try
 		{
 			string probe = Path.Combine(Application.StartupPath, "write_probe.tmp");
 			File.WriteAllText(probe, "1");
 			File.Delete(probe);
 		}
 		catch (Exception exw)
 		{
 			MessageBox.Show("Нет прав на запись в папку программы (" + Application.StartupPath + "). Настройки, логи и служебные файлы не будут сохраняться: " + exw.Message, "Табло", MessageBoxButtons.OK, MessageBoxIcon.Warning);
 		}
			this.VideoFilePath = string.Empty;
			this._timerRestore = new Multimedia.Timer();
			this._timerRestore.Period = 100;
			this._timerRestore.Resolution = 100;
			this._timerRestore.Mode = TimerMode.Periodic;
			this._timerRestore.SynchronizingObject = this;
			this._timerRestore.Tick += this._timerRestore_Tick;
			// ===== БЛОК 3b: сбой создания окна настроек не должен срывать запуск табло =====
			// Раньше любая ошибка config.xml бросала исключение здесь, остаток frmMain_Load
			// не выполнялся — и область табло оставалась пустой. Теперь ошибка пишется в
			// crash_log.txt, а загрузка программы продолжается.
			try
			{
				this._settingForm = new SettingForm();
			}
			catch (Exception exsf)
			{
				TabloPatch.CrashLog.Append("LOAD", "не удалось создать окно настроек: " + exsf.ToString());   // БЛОК 4, F8-06
				MessageBox.Show("Не удалось создать окно настроек при старте: " + exsf.Message + "\r\nТабло продолжит работу. Подробности записаны в crash_log.txt в папке программы.", "Табло", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
			this.FONT_DATA_Main = new byte[FontDataSize];
			string fontPath = Patch_ResolveFontPath();
			if (!this.FontLoad(fontPath, ref this.FONT_DATA_Main))
			{
				MessageBox.Show("Не удалось загрузить шрифт:\r\n" + fontPath + "\r\n\r\nБез него табло будет ПУСТЫМ. Скопируйте 16fs_new.fdat в папку программы или укажите путь в Настройках.", "Табло — нет шрифта", MessageBoxButtons.OK, MessageBoxIcon.Stop);
			}
			//Patch_TrySynthesizeYo();   // БЛОК 1c (Q4a): синтез не сработал на реальном полотне - вместо него замена Ё->Е в Patch_BoardText
			this.CreateGraphString();
			this.Initial();
			this._ControlboardTimer = new System.Windows.Forms.Timer();
			this._ControlboardTimer.Interval = 5000;
			this._ControlboardTimer.Tick += this._ControlboardTimer_Tick;
			this.InitialControlBoard();
			this.dTP_GameTime.Value = DateTime.Parse("00:00:00");
			Label label = new Label();
			label.Name = "lSpeed";
			label.Text = "×";
			label.Size = new Size(16, 20);
			label.Location = new Point(this.but_TimeReset.Left, this.but_TimeReset.Bottom + 6);
			ComboBox comboBox = new ComboBox();
			comboBox.Name = "cbSpeed";
			comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
			comboBox.Items.AddRange(new object[]
 			{
 				"0.8",
 				"0.9",
 				"1",
 				"1.1",
 				"1.15",
 				"1.2",
 				"1.25",
 				"1.3",
 				"1.4",
 				"1.5",
 				"1.75",
 				"2"
 			});
			comboBox.SelectedItem = "1";
			comboBox.Size = new Size(64, 21);
			comboBox.Location = new Point(this.but_TimeReset.Left + 18, this.but_TimeReset.Bottom + 4);
			this.but_TimeReset.Parent.Controls.Add(label);
			this.but_TimeReset.Parent.Controls.Add(comboBox);
			comboBox.BringToFront();
			label.BringToFront();
			TabPage tabPage = new TabPage("Рекламная пауза");
			tabPage.Name = "tpAdList";
			// БЛОК 4, F4-11: список занимает ВСЮ вкладку (Dock=Fill), кнопки живут
			// в нижней панели (Dock=Bottom). Раньше список был Dock=Top высотой 260,
			// а кнопки стояли по фиксированным координатам — при изменении размера
			// окна они «уезжали» или перекрывались списком.
			Panel adBottomPanel = new Panel();
			adBottomPanel.Dock = DockStyle.Bottom;
			adBottomPanel.Height = 34;
			CheckedListBox checkedListBox = new CheckedListBox();
			checkedListBox.Name = "cLB_AdVideoFiles";
			checkedListBox.Dock = DockStyle.Fill;
			// БЛОК 6, З4: Drag and Drop — ТОЛЬКО в окне «Рекламная пауза»:
			// перемещение файлов списка между собой (порядок воспроизведения).
			// Отметки «галочек» при переносе сохраняются. Схема перетаскивания —
			// MouseDown/MouseMove + DoDragDrop (порог — системный DragSize).
			int[] adDragIdx = new int[1];
			bool[] adDragging = new bool[1];
			Point[] adDragStart = new Point[1];
			adDragIdx[0] = -1;
			checkedListBox.AllowDrop = true;
			checkedListBox.MouseDown += delegate(object sMD, MouseEventArgs eMD)
			{
				if (eMD.Button == MouseButtons.Left)
				{
					adDragIdx[0] = checkedListBox.IndexFromPoint(eMD.Location);
					adDragStart[0] = new Point(eMD.X, eMD.Y);
				}
			};
			checkedListBox.MouseMove += delegate(object sMM, MouseEventArgs eMM)
			{
				if (eMM.Button != MouseButtons.Left || adDragIdx[0] < 0 || adDragging[0])
				{
					return;
				}
				if (Math.Abs(eMM.X - adDragStart[0].X) < SystemInformation.DragSize.Width &&
				Math.Abs(eMM.Y - adDragStart[0].Y) < SystemInformation.DragSize.Height)
				{
					return;   // движение меньше порога перетаскивания
				}
				adDragging[0] = true;
				try
				{
					checkedListBox.DoDragDrop(checkedListBox.Items[adDragIdx[0]], DragDropEffects.Move);
				}
				catch (Exception exDrag)
				{
					TabloPatch.Diag.Note("DragDrop рекламная пауза", exDrag);
				}
				adDragging[0] = false;
				adDragIdx[0] = -1;
			};
			checkedListBox.DragOver += delegate(object sDO, DragEventArgs eDO)
			{
				if (adDragging[0] && (eDO.AllowedEffect & DragDropEffects.Move) == DragDropEffects.Move)
				{
					eDO.Effect = DragDropEffects.Move;
				}
				else
				{
					eDO.Effect = DragDropEffects.None;
				}
			};
			checkedListBox.DragDrop += delegate(object sDD, DragEventArgs eDD)
			{
				try
				{
					if (!adDragging[0] || adDragIdx[0] < 0)
					{
						return;
					}
					Point ptAd = checkedListBox.PointToClient(new Point(eDD.X, eDD.Y));
					int targetAd = checkedListBox.IndexFromPoint(ptAd);
					if (targetAd < 0)
					{
						targetAd = checkedListBox.Items.Count - 1;   // бросили ниже списка — в конец
					}
					int srcAd = adDragIdx[0];
					if (targetAd < 0 || targetAd == srcAd)
					{
						return;
					}
					object movedAd = checkedListBox.Items[srcAd];
					CheckState csAd = checkedListBox.GetItemCheckState(srcAd);
					checkedListBox.Items.RemoveAt(srcAd);
					if (targetAd > checkedListBox.Items.Count)
					{
						targetAd = checkedListBox.Items.Count;
					}
					checkedListBox.Items.Insert(targetAd, movedAd);
					checkedListBox.SetItemCheckState(targetAd, csAd);
					checkedListBox.SelectedIndex = targetAd;
				}
				catch (Exception exDrop)
				{
					TabloPatch.Diag.Note("DragDrop перемещение", exDrop);
				}
			};
			tabPage.Controls.Add(checkedListBox);
			tabPage.Controls.Add(adBottomPanel);
			Button button = new Button();
			button.Text = "Доб.";   // БЛОК 6, З4: сокращённое название — все кнопки влезают в панель
			button.Size = new Size(54, 24);
			button.Location = new Point(6, 5);   // БЛОК 4, F4-11: в нижней панели
			button.Click += delegate(object s2, EventArgs e2)
			{
				Control[] array7 = this.tabControl_VideoFileList.Controls.Find("cLB_AdVideoFiles", true);
				if (array7.Length == 0)
				{
					return;
				}
				OpenFileDialog openFileDialog = new OpenFileDialog();
				openFileDialog.Filter = "Media Files|*.mpg;*.avi;*.wma;*.mov;*.wav;*.mp2;*.mp3;*.flv;*.mp4;*.mkv;*.m4v;*.ts;*.mpeg|All Files|*.*";
				openFileDialog.Multiselect = true;
				if (openFileDialog.ShowDialog() == DialogResult.OK)
				{
					foreach (string item in openFileDialog.FileNames)
					{
						((CheckedListBox)array7[0]).Items.Add(item);
					}
				}
			};
			adBottomPanel.Controls.Add(button);
			// БЛОК 6, З4: кнопка «Удал.» — убирает из списка отмеченные файлы,
			// а если ничего не отмечено — текущую выбранную строку.
			Button buttonDel = new Button();
			buttonDel.Text = "Удал.";
			buttonDel.Size = new Size(54, 24);
			buttonDel.Location = new Point(64, 5);
			buttonDel.Click += delegate(object s4, EventArgs e4)
			{
				Control[] arrayDel = this.tabControl_VideoFileList.Controls.Find("cLB_AdVideoFiles", true);
				if (arrayDel.Length == 0)
				{
					return;
				}
				CheckedListBox lbAd = (CheckedListBox)arrayDel[0];
				try
				{
					if (lbAd.CheckedItems.Count > 0)
					{
						List<object> toRemove = new List<object>();
						foreach (object oChk in lbAd.CheckedItems)
						{
							toRemove.Add(oChk);
						}
						for (int ri = 0; ri < toRemove.Count; ri++)
						{
							lbAd.Items.Remove(toRemove[ri]);
						}
					}
					else if (lbAd.SelectedIndex >= 0)
					{
						lbAd.Items.RemoveAt(lbAd.SelectedIndex);
					}
				}
				catch (Exception exDelAd)
				{
					TabloPatch.Diag.Note("Удаление из списка рекламной паузы", exDelAd);
				}
			};
			adBottomPanel.Controls.Add(buttonDel);
			Button button2 = new Button();
			button2.Text = "Зап. выбр.";   // БЛОК 6, З4: сокращённое название
			button2.Size = new Size(86, 24);
			button2.Location = new Point(122, 5);   // БЛОК 4, F4-11: в нижней панели
			button2.Click += delegate(object s2, EventArgs e2)
			{
				TabPage tabPage2 = null;
				foreach (object obj9 in this.tabControl_VideoFileList.TabPages)
				{
					TabPage tabPage3 = (TabPage)obj9;
					if (tabPage3.Name == "tpAdList")
					{
						tabPage2 = tabPage3;
						break;
					}
				}
				if (tabPage2 == null)
				{
					return;
				}
				Control[] array7 = tabPage2.Controls.Find("cLB_AdVideoFiles", true);
				if (array7.Length == 0)
				{
					return;
				}
				CheckedListBox checkedListBox2 = (CheckedListBox)array7[0];
				if (tabPage2.Tag == null)
				{
					Control control10 = tabPage2;
					object[] array8 = new object[3];
					array8[0] = this._TabloMode;
					array8[1] = this._NowPlayList;
					control10.Tag = array8;
				}
				object[] array9 = tabPage2.Tag as object[];
				CheckedListBox checkedListBox3 = new CheckedListBox();
				foreach (object item in checkedListBox2.CheckedItems)
				{
					checkedListBox3.Items.Add(item);
				}
				if (checkedListBox3.Items.Count == 0)
				{
					foreach (object item2 in checkedListBox2.Items)
					{
						checkedListBox3.Items.Add(item2);
					}
				}
				if (checkedListBox3.Items.Count == 0)
				{
					return;
				}
				array9[2] = checkedListBox3;
				try
				{
					if (this.vpf != null)
					{
						this.CloseVideoPlayer();
					}
					this.OpenVideo(checkedListBox3);
				}
				catch
				{
				}
			};
			adBottomPanel.Controls.Add(button2);
			Button button3 = new Button();
			button3.Text = "Ост.";   // БЛОК 6, З4: сокращённое название — кнопка теперь ВИДНА в панели
			button3.Size = new Size(60, 24);
			button3.Location = new Point(212, 5);   // БЛОК 4, F4-11: в нижней панели
			button3.Click += delegate(object s3, EventArgs e3)
			{
				TabPage tabPage2 = null;
				foreach (object obj9 in this.tabControl_VideoFileList.TabPages)
				{
					TabPage tabPage3 = (TabPage)obj9;
					if (tabPage3.Name == "tpAdList")
					{
						tabPage2 = tabPage3;
						break;
					}
				}
				if (tabPage2 == null || tabPage2.Tag == null)
				{
					return;
				}
				object[] array7 = tabPage2.Tag as object[];
				tabPage2.Tag = null;
				try
				{
					this.CloseVideoPlayer();
					if (array7 != null && array7.Length == 3)
					{
						this._TabloMode = (frmMain.tabloMode)array7[0];
						CheckedListBox checkedListBox2 = array7[1] as CheckedListBox;
						if (checkedListBox2 != null && checkedListBox2.Items.Count > 0)
						{
							try
							{
								this.OpenVideo(checkedListBox2);
							}
							catch
							{
							}
						}
						this.TabloMode();
					}
				}
				catch
				{
				}
			};
			adBottomPanel.Controls.Add(button3);
			// БЛОК 6, З4: «Рекламная пауза» показывается ПЕРВОЙ при запуске программы —
			// вкладка вставляется в начало и сразу выбирается.
			this.tabControl_VideoFileList.TabPages.Insert(0, tabPage);
			this.tabControl_VideoFileList.SelectedTab = tabPage;
			// ПУНКТ 2: перетаскивание (изменение порядка) включается во ВСЕХ ТРЁХ окнах
			// списка видео — «Рекламная пауза» уже умела, добавляем «Перерыв» и «Таймаут».
			// Список сценариев тоже получает перетаскивание: кнопки «Вверх/Вниз» в нём
			// скрыты, поэтому другого способа поменять порядок сценариев не осталось.
			this.Patch_EnableReorderDragDrop(this.cLB_VideoFiles);
			this.Patch_EnableReorderDragDrop(this.cLB_TimeoutVideoFiles);
			this.Patch_EnableReorderDragDrop(this.cLB_ScriptFiles);
			this.nUD_Period.Maximum = 9m;
			Control[] array = base.Controls.Find("cLB_ScriptFiles", true);
			if (array.Length != 0)
			{
				GroupBox groupBox = array[0].Parent as GroupBox;
				if (groupBox == null && array[0].Parent != null)
				{
					groupBox = (array[0].Parent.Parent as GroupBox);
				}
				if (groupBox != null)
				{
					Control[] array2 = new Control[groupBox.Parent.Controls.Count];
					groupBox.Parent.Controls.CopyTo(array2, 0);
					foreach (Control control in array2)
					{
						if (control.Name == "lbTimeArchive" || control.Name == "butApplySnapshot" || control.Name == "gBox_TimeArchive" || (control is Label && control.Text == "Архив времени"))
						{
							groupBox.Parent.Controls.Remove(control);
						}
					}
					int height = (groupBox.Bottom - groupBox.Top) / 2;
					groupBox.Dock = DockStyle.None;
					groupBox.Anchor = (AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
					groupBox.SetBounds(groupBox.Left, groupBox.Top, groupBox.Width, height);
					array[0].Dock = DockStyle.Fill;
					int num = groupBox.Bottom + 6;
					int num2 = num + 24;
					foreach (object obj in groupBox.Parent.Controls)
					{
						Control control2 = (Control)obj;
						if (control2 is Button && (control2.Text == "Добавить" || control2.Text == "Вверх" || control2.Text == "Вниз" || control2.Text == "Удалить") && control2.Parent == groupBox.Parent && control2.Top >= groupBox.Bottom - 4)
						{
							control2.Dock = DockStyle.None;
							control2.Anchor = (AnchorStyles.Top | AnchorStyles.Left);
							control2.Top = num;
						}
					}
					foreach (object obj2 in groupBox.Controls)
					{
						Control control3 = (Control)obj2;
						if (control3 is Button && (control3.Text == "Добавить" || control3.Text == "Вверх" || control3.Text == "Вниз" || control3.Text == "Удалить"))
						{
							control3.Dock = DockStyle.None;
							control3.Anchor = (AnchorStyles.Bottom | AnchorStyles.Left);
							control3.Top = groupBox.ClientSize.Height - control3.Height - 6;
						}
					}
					int num3 = num2 + 6;
					int num4 = groupBox.Parent.ClientSize.Height - num3 - 6;
					if (num4 < 140)
					{
						num4 = 140;
					}
					GroupBox groupBox2 = new GroupBox();
					groupBox2.Name = "gBox_TimeArchive";
					groupBox2.Text = "Архив времени";
					groupBox2.Dock = DockStyle.None;
					groupBox2.Anchor = (AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
					groupBox2.SetBounds(groupBox.Left, num3, groupBox.Width, num4);
					ListBox listBox = new ListBox();
					listBox.Name = "lbTimeArchive";
					listBox.SelectionMode = SelectionMode.One;
					listBox.Anchor = (AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
					listBox.Location = new Point(8, 20);
					listBox.Size = new Size(groupBox2.Width - 16, groupBox2.Height - 62);
					listBox.SelectedIndexChanged += delegate(object s2, EventArgs e2)
					{
						Control[] array7 = this.Controls.Find("butApplySnapshot", true);
						if (array7.Length != 0)
						{
							array7[0].Enabled = (((ListBox)s2).SelectedIndex >= 0);
						}
					};
					Button button4 = new Button();
					button4.Name = "butApplySnapshot";
					button4.Text = "Применить время";
					button4.Size = new Size(140, 24);
					button4.Anchor = (AnchorStyles.Bottom | AnchorStyles.Left);
					button4.Location = new Point(8, groupBox2.Height - 32);
					button4.Enabled = false;
					button4.Click += delegate(object s3, EventArgs e3)
					{
						// БЛОК 4, F6-08: архив берётся из кэша (F9-05), строка снимка
						// разбирается ЗАЩИЩЁННО (Patch_TryParseSnapshot) — «битая» запись
						// больше не роняет обработчик молча, а диалог честно перечисляет
						// все последствия применения снимка.
						ListBox listBox3 = this.Patch_GetArchiveList();
						if (listBox3 == null || listBox3.SelectedIndex < 0)
						{
							return;
						}
						string text = listBox3.Items[listBox3.SelectedIndex] as string;
						if (text == null)
						{
							return;
						}
						// ПУНКТ 12: разбор и применение снимка вынесены в общий метод —
						// его же использует веб-панель (архив времени), логика одна.
						TimeSpan snapRemain;
						string[] snapPensA;
						string[] snapPensB;
						int snapPeriod;
						int snapScoreH;
						int snapScoreA;
						if (!this.Patch_TryParseSnapshot(text, out snapRemain, out snapPensA, out snapPensB, out snapPeriod, out snapScoreH, out snapScoreA))
						{
							TabloPatch.SerialPortLog.Write("АРХИВ: запись не разбирается, применение отменено: " + text);
							MessageBox.Show("Не удалось разобрать запись архива:\r\n" + text, "Табло — архив времени", MessageBoxButtons.OK, MessageBoxIcon.Warning);
							return;
						}
						string head = text.Split(new char[] { '|' })[0].Trim();
						// ПУНКТ 12: в подтверждении показываем и счёт с периодом из снимка
						string snapExtra = (snapPeriod > 0)
							? ("\r\nВ снимке: период " + snapPeriod.ToString() + ", счёт " + snapScoreH.ToString() + ":" + snapScoreA.ToString() + " (они НЕ изменяются).")
							: "";
						string dlg = "Применить снимок " + head + "?\r\n\r\nЧто произойдёт:\r\n"
							+ "• часы периода остановятся;\r\n"
							+ "• остаток времени периода станет " + FmtClock(snapRemain, false) + ";\r\n"
							+ "• ТЕКУЩИЕ удаления будут заменены удалениями из снимка (А: " + snapPensA.Length + ", Б: " + snapPensB.Length + ").\r\n\r\n"
							+ "Счёт и НОМЕР периода НЕ изменятся." + snapExtra;
						if (MessageBox.Show(dlg, "Табло — архив времени", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
						{
							return;
						}
						this.Patch_ApplySnapshotCore(text, "кнопка «Применить время»");
						listBox3.SelectedIndex = -1;
						Control[] array12 = this.Controls.Find("butApplySnapshot", true);
						if (array12.Length != 0)
						{
							array12[0].Enabled = false;
						}
					};
					groupBox2.Controls.Add(listBox);
					groupBox2.Controls.Add(button4);
					groupBox.Parent.Controls.Add(groupBox2);
					groupBox2.BringToFront();
				}
			}
			// БЛОК 6, З9: окно списка сценариев — остаются только «Доб.», «Удал.» и
			// «Показать в списке видео»; кнопки «Вверх/Вниз» убраны (порядок фиксируется
			// пунктом меню «Файл -> Сохранить порядок воспроизведения»).
			try
			{
				this.but_AddScript.Text = "Доб.";
				this.but_DeleteScript.Text = "Удал.";
				this.but_ScriptUp.Visible = false;
				this.but_ScriptDown.Visible = false;
				Button butShowInVideo = new Button();
				butShowInVideo.Name = "but_ScriptShowInVideo";
				butShowInVideo.Text = "Показать в списке видео";
				butShowInVideo.Size = new Size(139, 23);
				butShowInVideo.Location = new Point(77, 3);
				butShowInVideo.UseVisualStyleBackColor = true;
				butShowInVideo.Click += delegate(object sV, EventArgs eV)
				{
					this.Patch_ShowScriptsInVideoList();
				};
				this.panel_ScriptButtons.Controls.Add(butShowInVideo);
			}
			catch (Exception exScButtons)
			{
				TabloPatch.Diag.Note("Кнопки списка сценариев", exScButtons);
			}
			try
			{
				List<Control> list = new List<Control>();
				Stack<Control> stack = new Stack<Control>();
				stack.Push(this);
				while (stack.Count > 0)
				{
					foreach (object obj3 in stack.Pop().Controls)
					{
						Control control4 = (Control)obj3;
						stack.Push(control4);
						Button button5 = control4 as Button;
						if (button5 != null && button5.Tag is object[])
						{
							list.Add(control4);
						}
						else if (control4.Name != null && (control4.Name.StartsWith("pnlPen") || control4.Name.StartsWith("tbPenNum")))
						{
							list.Add(control4);
						}
					}
				}
				foreach (Control control5 in list)
				{
					if (control5.Parent != null)
					{
						control5.Parent.Controls.Remove(control5);
					}
				}
			}
			catch
			{
			}
			Control[] array4 = new Control[]
			{
				this.lB_DelPeople_A,
				this.lB_DelPeople_B
			};
			string[] array5 = new string[]
			{
				"penDelA",
				"penDelB"
			};
			string[] array6 = new string[]
			{
				"penAddA",
				"penAddB"
			};
			MouseEventHandler cachedDel8 = null;
			for (int j = 0; j < 2; j++)
			{
				try
				{
					ListBox listBox2 = (ListBox)array4[j];
					Control parent = listBox2.Parent;
					Button addBtnD = null;
					foreach (object obj4 in parent.Controls)
					{
						Control control6 = (Control)obj4;
						if (addBtnD == null && control6 is Button && control6.Text != null && control6.Text.StartsWith("Доб"))
						{
							addBtnD = (Button)control6;
						}
					}
					if (addBtnD != null)
					{
						addBtnD.Name = array6[j];
						addBtnD.Text = "Доб.";
						Control plateD = null;
						Rectangle rect = listBox2.RectangleToScreen(new Rectangle(0, listBox2.Height - 6, listBox2.Width, 46));
						Stack<Control> stack2 = new Stack<Control>();
						stack2.Push(this);
						while (stack2.Count > 0)
						{
							foreach (object obj5 in stack2.Pop().Controls)
							{
								Control control7 = (Control)obj5;
								stack2.Push(control7);
								if (control7.RectangleToScreen(control7.Bounds).IntersectsWith(rect) && plateD == null && control7 is TextBox)
								{
									plateD = control7;
								}
							}
						}
						bool[] pressed = new bool[1];
						addBtnD.MouseDown += delegate(object sA, MouseEventArgs eA)
						{
							if (eA.Button == MouseButtons.Left && plateD != null)
							{
								try
								{
									plateD.Focus();
									SendKeys.Send("{ENTER}");
								}
								catch
								{
								}
							}
						};
						System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
						timer.Interval = 120;
						timer.Tick += delegate(object sP, EventArgs eP)
						{
							try
							{
								if (Control.MouseButtons != MouseButtons.Left)
								{
									pressed[0] = false;
								}
								else if (!pressed[0] && plateD != null)
								{
									if (addBtnD.RectangleToScreen(new Rectangle(0, 0, addBtnD.Width, addBtnD.Height)).Contains(Cursor.Position))
									{
										pressed[0] = true;
										plateD.Focus();
										SendKeys.Send("{ENTER}");
									}
								}
							}
							catch
							{
							}
						};
						timer.Start();
						int width = addBtnD.Width;
						int left = addBtnD.Left;
						int top = addBtnD.Top;
						int height2 = addBtnD.Height;
						addBtnD.Width = width / 2 - 1;
						Button button6 = new Button();
						button6.Name = array5[j];
						button6.Text = "Удал.";
						button6.Size = new Size(width / 2 - 1, height2);
						button6.Location = new Point(left + width / 2 + 1, top);
						Control control8 = button6;
						MouseEventHandler value;
						if ((value = cachedDel8) == null)
						{
							value = (cachedDel8 = delegate(object s5, MouseEventArgs e5)
							{
								try
								{
									if (e5.Button == MouseButtons.Left)
									{
										Button button7 = (Button)s5;
										ListBox listBox3 = (button7.Name == "penDelA") ? this.lB_DelPeople_A : this.lB_DelPeople_B;
										int selectedIndex = listBox3.SelectedIndex;
										if (selectedIndex >= 0 && selectedIndex < listBox3.Items.Count)
										{
											listBox3.Items.RemoveAt(selectedIndex);
										}
										if (button7.Name == "penDelA")
										{
											this.ShowPenalty(0, this.lB_DelPeople_A, this.gs_LWindow_2);
											this.ShowPenalty(1, this.lB_DelPeople_A, this.gs_LWindow_3);
										}
										else
										{
											this.ShowPenalty(0, this.lB_DelPeople_B, this.gs_RWindow_2);
											this.ShowPenalty(1, this.lB_DelPeople_B, this.gs_RWindow_3);
										}
										this.TabloMode();
										this.statusShow();
									}
								}
								catch
								{
								}
							});
						}
						control8.MouseDown += value;
						parent.Controls.Add(button6);
						button6.BringToFront();
					}
				}
				catch
				{
				}
			}
						try
			{
				MenuStrip msDev = null;
				foreach (object oDev in base.Controls)
				{
					Control cDev = oDev as Control;
					if (cDev is MenuStrip)
					{
						msDev = (MenuStrip)cDev;
						break;
					}
				}
				if (msDev != null)
				{
					ToolStripMenuItem devItem = new ToolStripMenuItem("Режим разработчика");
					devItem.Click += this.Patch_DevMenu_Click;
					msDev.Items.Add(devItem);
					// БЛОК 4, F5-09: горячие клавиши теперь ВИДНЫ в меню (раньше о них
					// можно было узнать только из кода/лога). Обработка клавиш осталась
					// в ProcessCmdKey — здесь только отображение сочетаний и дубль действий.
					ToolStripMenuItem svcItem = new ToolStripMenuItem("Сервис");
					ToolStripMenuItem hkPult = new ToolStripMenuItem("Переподключить пульт");
					hkPult.ShortcutKeyDisplayString = "Ctrl+F7";
					hkPult.Click += delegate
					{
						TabloPatch.SerialPortLog.Write("МЕНЮ: переподключение пульта (аналог Ctrl+F7)");
						this.Patch_ReconnectPult();
					};
					ToolStripMenuItem hkExt = new ToolStripMenuItem("Окно доп. вывода");
					hkExt.ShortcutKeyDisplayString = "Ctrl+F8";
					hkExt.Click += delegate
					{
						TabloPatch.ExtOutUi.Open();
					};
					ToolStripMenuItem hkEmu = new ToolStripMenuItem("Эмулятор пульта");
					hkEmu.ShortcutKeyDisplayString = "Ctrl+F9";
					hkEmu.Click += delegate
					{
						TabloPatch.PatchEntry.OpenEmulator(this);
					};
					svcItem.DropDownItems.Add(hkPult);
					svcItem.DropDownItems.Add(hkExt);
					svcItem.DropDownItems.Add(hkEmu);
					msDev.Items.Add(svcItem);
				}
			}
			catch
			{
			}
			System.Windows.Forms.Timer wdT = new System.Windows.Forms.Timer();
			wdT.Interval = 250;
			wdT.Tick += delegate(object sW, EventArgs eW)
			{
				try
				{
					bool flag = false;
					ListBox[] array7 = new ListBox[]
					{
						this.lB_DelPeople_A,
						this.lB_DelPeople_B
					};
					for (int k = 0; k < 2; k++)
					{
						ListBox listBox3 = array7[k];
						if (listBox3.Items.Count != 0)
						{
							int[] array8 = listBox3.Tag as int[];
							if (array8 == null || array8.Length != 6)
							{
								array8 = new int[6];
								array8[0] = -1;
								array8[3] = -1;
								listBox3.Tag = array8;
							}
							for (int l = 0; l < 2; l++)
							{
								if (array8[l * 3] >= 0)
								{
									bool flag2 = false;
									int num5 = 0;
									while (num5 < listBox3.Items.Count && num5 < 2)
									{
										string text = (string)listBox3.Items[num5];
										if (text != null && text.Length >= 2 && int.Parse(text.Substring(0, 2)) == array8[l * 3])
										{
											flag2 = true;
											break;
										}
										num5++;
									}
									if (!flag2)
									{
										array8[l * 3] = -1;
									}
								}
							}
							int num6 = 0;
							while (num6 < listBox3.Items.Count && num6 < 2)
							{
								string text2 = (string)listBox3.Items[num6];
								if (text2 != null && text2.Length >= 8)
								{
									int num7 = int.Parse(text2.Substring(0, 2));
									int num8 = int.Parse(text2.Substring(3, 2)) * 60 + int.Parse(text2.Substring(6, 2));
									int num9 = -1;
									for (int m = 0; m < 2; m++)
									{
										if (array8[m * 3] == num7)
										{
											num9 = m;
											break;
										}
									}
									if (num9 < 0)
									{
										for (int n = 0; n < 2; n++)
										{
											if (array8[n * 3] < 0)
											{
												num9 = n;
												break;
											}
										}
									}
									if (num9 >= 0)
									{
										if (array8[num9 * 3] < 0)
										{
											array8[num9 * 3] = num7;
											array8[num9 * 3 + 1] = num8;
											array8[num9 * 3 + 2] = num8;
											flag = true;
										}
										else
										{
											int num10 = array8[num9 * 3 + 1];
											int num11 = array8[num9 * 3 + 2];
											if (num10 == -2)
											{
												array8[num9 * 3 + 2] = num8;
											}
											else if (num8 == num10)
											{
												flag = true;
												array8[num9 * 3 + 2] = num8;
											}
											else
											{
												int num12 = num11 - num8;
												if (num12 == 0 || num12 == 1 || num12 == 2)
												{
													array8[num9 * 3 + 1] = -2;
													array8[num9 * 3 + 2] = num8;
												}
												else if (num11 == num10)
												{
													array8[num9 * 3 + 1] = num8;
													array8[num9 * 3 + 2] = num8;
													flag = true;
												}
												else
												{
													array8[num9 * 3 + 1] = -2;
													array8[num9 * 3 + 2] = num8;
												}
											}
										}
									}
								}
								num6++;
							}
						}
					}
					wdT.Interval = (flag ? 250 : 2000);
				}
				catch
				{
				}
			};
			wdT.Start();
			try
			{
				string bkRoot = Path.Combine(Application.StartupPath, "backup");
				EventHandler onClick = delegate(object sB, EventArgs eB)
				{
					try
					{
						Directory.CreateDirectory(bkRoot);
						string text = Path.Combine(bkRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bkt");
						List<string> list2 = new List<string>();
						foreach (string path in Directory.GetFiles(Application.StartupPath))
						{
							string text2 = Path.GetFileName(path).ToLowerInvariant();
							if ((text2.EndsWith(".txt") || text2.EndsWith(".ini") || text2.EndsWith(".xml") || text2.EndsWith(".cfg")) && !text2.Contains("debug") && !text2.Contains("crush") && text2 != "tablo_time.txt")
							{
								list2.Add(Path.GetFileName(path));
							}
						}
						FileStream fileStream = new FileStream(text, FileMode.Create, FileAccess.Write);
						try
						{
							BinaryWriter binaryWriter = new BinaryWriter(fileStream);
							try
							{
								binaryWriter.Write(list2.Count);
								for (int l = 0; l < list2.Count; l++)
								{
									byte[] array7 = File.ReadAllBytes(Path.Combine(Application.StartupPath, list2[l]));
									binaryWriter.Write(list2[l]);
									binaryWriter.Write(array7.Length);
									binaryWriter.Write(array7);
								}
							}
							finally
							{
								binaryWriter.Close();
							}
						}
						finally
						{
							fileStream.Close();
						}
						MessageBox.Show("Копия создана:\n" + text, "Табло", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					}
					catch (Exception ex)
					{
						MessageBox.Show("Не удалось создать копию: " + ex.Message, "Табло", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					}
				};
				EventHandler onClick2 = delegate(object sB, EventArgs eB)
				{
					try
					{
						OpenFileDialog openFileDialog = new OpenFileDialog();
						openFileDialog.Filter = "Резервные копии Табло|*.bkt|Все файлы|*.*";
						openFileDialog.Title = "Выберите файл резервной копии";
						if (Directory.Exists(bkRoot))
						{
							openFileDialog.InitialDirectory = bkRoot;
						}
						if (openFileDialog.ShowDialog() == DialogResult.OK)
						{
							if (MessageBox.Show("Восстановить настройки из файла\n" + Path.GetFileName(openFileDialog.FileName) + "?\nТекущие служебные файлы будут перезаписаны.\nПосле этого перезапустите программу.", "Табло", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.Yes)
							{
								FileStream fileStream = new FileStream(openFileDialog.FileName, FileMode.Open, FileAccess.Read);
								try
								{
									BinaryReader binaryReader = new BinaryReader(fileStream);
									try
									{
										int num5 = binaryReader.ReadInt32();
										for (int k = 0; k < num5; k++)
										{
											string path = binaryReader.ReadString();
											int count = binaryReader.ReadInt32();
											byte[] bytes = binaryReader.ReadBytes(count);
											string fileName = Path.GetFileName(path);
											File.WriteAllBytes(Path.Combine(Application.StartupPath, fileName), bytes);
										}
									}
									finally
									{
										binaryReader.Close();
									}
								}
								finally
								{
									fileStream.Close();
								}
								MessageBox.Show("Восстановление завершено. Перезапустите программу.", "Табло", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							}
						}
					}
					catch (Exception ex)
					{
						MessageBox.Show("Не удалось восстановить копию: " + ex.Message, "Табло", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					}
				};
				MenuStrip menuStrip = null;
				foreach (object obj6 in base.Controls)
				{
					Control control9 = obj6 as Control;
					if (control9 is MenuStrip)
					{
						menuStrip = (MenuStrip)control9;
						break;
					}
				}
				if (menuStrip != null)
				{
					ToolStripMenuItem toolStripMenuItem = null;
					foreach (object obj7 in menuStrip.Items)
					{
						ToolStripItem toolStripItem = (ToolStripItem)obj7;
						if (toolStripItem.Text != null && toolStripItem.Text.Replace("&", "") == "Сервис")
						{
							toolStripMenuItem = (toolStripItem as ToolStripMenuItem);
							break;
						}
					}
					if (toolStripMenuItem == null)
					{
						toolStripMenuItem = new ToolStripMenuItem("Сервис");
						menuStrip.Items.Add(toolStripMenuItem);
					}
					ToolStripMenuItem toolStripMenuItem2 = new ToolStripMenuItem("Резервные копии");
					toolStripMenuItem2.DropDownItems.Add(new ToolStripMenuItem("Создать копию…", null, onClick));
					toolStripMenuItem2.DropDownItems.Add(new ToolStripMenuItem("Восстановить копию из файла…", null, onClick2));
					toolStripMenuItem.DropDownItems.Insert(0, toolStripMenuItem2);   // БЛОК 6, З5: «Резервные копии» — первый пункт «Сервиса»
				}
				else if (base.Menu != null)
				{
					MenuItem menuItem = null;
					foreach (object obj8 in base.Menu.MenuItems)
					{
						MenuItem menuItem2 = (MenuItem)obj8;
						if (menuItem2.Text != null && menuItem2.Text.Replace("&", "") == "Сервис")
						{
							menuItem = menuItem2;
							break;
						}
					}
					if (menuItem == null)
					{
						menuItem = new MenuItem("Сервис");   // БЛОК 6, З5: перенос из «Файл» в «Сервис»
						base.Menu.MenuItems.Add(menuItem);
					}
					MenuItem menuItem3 = new MenuItem("Резервные копии");
					menuItem3.MenuItems.Add(new MenuItem("Создать копию…", onClick));
					menuItem3.MenuItems.Add(new MenuItem("Восстановить копию из файла…", onClick2));
					menuItem.MenuItems.Add(menuItem3);
				}
			}
			catch
			{
			}
			// БЛОК 6, З9: пункт меню «Файл -> Сохранить порядок воспроизведения».
			// Фиксирует текущий порядок списка сценариев в его файле (.spf).
			try
			{
				MenuStrip msOrd = null;
				foreach (object oOrd in base.Controls)
				{
					Control cOrd = oOrd as Control;
					if (cOrd is MenuStrip)
					{
						msOrd = (MenuStrip)cOrd;
						break;
					}
				}
				if (msOrd != null)
				{
					ToolStripMenuItem fileOrd = null;
					foreach (object oFi in msOrd.Items)
					{
						ToolStripItem tiFi = oFi as ToolStripItem;
						if (tiFi != null && tiFi.Text != null && tiFi.Text.Replace("&", "") == "Файл")
						{
							fileOrd = tiFi as ToolStripMenuItem;
							break;
						}
					}
					if (fileOrd == null)
					{
						fileOrd = new ToolStripMenuItem("Файл");
						msOrd.Items.Add(fileOrd);
					}
					ToolStripMenuItem miSaveOrder = new ToolStripMenuItem("Сохранить порядок воспроизведения");
					miSaveOrder.Click += delegate(object sOrd, EventArgs eOrd)
					{
						this.Patch_SaveScriptOrder();
					};
					int insOrd = fileOrd.DropDownItems.Count;
					for (int qOrd = 0; qOrd < fileOrd.DropDownItems.Count; qOrd++)
					{
						ToolStripItem itOrd = fileOrd.DropDownItems[qOrd] as ToolStripItem;
						if (itOrd != null && itOrd.Text != null && itOrd.Text.Replace("&", "").Replace("…", "") == "Выход")
						{
							insOrd = qOrd;
							break;
						}
					}
					fileOrd.DropDownItems.Insert(insOrd, miSaveOrder);
				}
			}
			catch
			{
			}
		}

		// Token: 0x06000049 RID: 73 RVA: 0x00002310 File Offset: 0x00000510
		private void Form1_FormClosed(object sender, FormClosedEventArgs e)
		{
			// ===== БЛОК 2, F8-01: корректное закрытие без краха =====
			// Было: winmm-таймеры (_breakTimer/_timeoutTimer/_promptTimer/_timerRestore)
			// не освобождались — закрытие программы ВО ВРЕМЯ ПЕРЕРЫВА/ТАЙМ-АУТА могло
			// уронить процесс (callback продолжал дёргать BeginInvoke на закрытой форме).
			// Стало: все таймеры глушатся ДО разрушения формы + защита внутри
			// Multimedia.Timer (см. Timer.cs). Пульт: протокол и DLL не изменены (Q5);
			// Dispose порта — как раньше, но под try/catch: "отвалившийся" USB-переходник
			// больше не мешает закрытию. Ошибки выхода пишутся в crash_log.txt (F8-03).
			try
			{
				try
				{
					if (this._patchStateTimer != null)
					{
						this._patchStateTimer.Stop();
					}
				}
				catch
				{
				}
				try
				{
					this.Cmd_Stop();
				}
				catch
				{
				}
				Patch_KillTimer(ref this._promptTimer);
				Patch_KillTimer(ref this._gameTimer);
				Patch_KillTimer(ref this._timeoutTimer);
				Patch_KillTimer(ref this._breakTimer);
				Patch_KillTimer(ref this._timerRestore);
				try
				{
					this.Patch_AutosaveState(true);   // БЛОК 2, F6-04: финальный снимок матча
				}
				catch
				{
				}
				try
				{
					TabloPatch.WebServer.Stop();
				}
				catch
				{
				}
				try
				{
					TabloPatch.ExtOut.StopAll();
				}
				catch
				{
				}
				try
				{
					this.SavingCommandName();   // F8-02: Save() сам пропустит запись, если XML не изменился
				}
				catch
				{
				}
				try
				{
					if (this._Controlboard != null)
					{
						this._Controlboard.Dispose();
						this._Controlboard = null;
					}
				}
				catch
				{
				}
				try
				{
					this.CloseVideoPlayer();
				}
				catch
				{
				}
				try
				{
					this.CloseTabloHockey();
				}
				catch
				{
				}
				try
				{
					this.CloseTabloHockeyPrompt();
				}
				catch
				{
				}
				try
				{
					this.CloseBackgroundWindow();
				}
				catch
				{
				}
				// БЛОК 4, F8-09: освобождаем WebClient для vMix (Q22a: сама интеграция
				// не тронута — только корректное освобождение ресурса при выходе)
				try
				{
					if (this._vmixClient != null)
					{
						this._vmixClient.Dispose();
						this._vmixClient = null;
					}
				}
				catch
				{
				}
				// БЛОК 4, F8-05: закрываем писатель лога пульта (файл освобождается)
				try
				{
					TabloPatch.SerialPortLog.Close();
				}
				catch
				{
				}
			}
			catch (Exception ex)
			{
				// БЛОК 4, F8-06: единая точка записи crash_log (ротация + %TEMP% при отсутствии прав)
				TabloPatch.CrashLog.Append("EXIT", ex.ToString());
			}
		}

		// Token: 0x0600004A RID: 74 RVA: 0x00002350 File Offset: 0x00000550
		private void frmMain_Paint(object sender, PaintEventArgs e)
		{
		}

		// Token: 0x0600004B RID: 75 RVA: 0x0000926C File Offset: 0x0000746C
		private void Common_but_AddVideo_Click(object sender, EventArgs e, ListBox lb)
		{
			// ПУНКТ 2: загрузка НЕСКОЛЬКИХ файлов сразу — как в «Рекламной паузе».
			// Метод общий для окон «Перерыв» и «Таймаут», поэтому достаточно одной правки.
			OpenFileDialog openFileDialog = new OpenFileDialog();
			openFileDialog.Filter = "Media Files|*.mpg;*.avi;*.wma;*.mov;*.wav;*.mp2;*.mp3;*.flv;*.mp4;*.mkv;*.m4v;*.ts;*.mpeg|All Files|*.*";
			openFileDialog.Multiselect = true;
			if (DialogResult.OK == openFileDialog.ShowDialog())
			{
				int n = 0;
				foreach (string fn in openFileDialog.FileNames)
				{
					if (fn == null || fn.Length == 0)
					{
						continue;
					}
					lb.Items.Add(fn);
					n++;
				}
				TabloPatch.SerialPortLog.Write("СПИСОК ВИДЕО (" + lb.Name + "): добавлено файлов: " + n.ToString());
			}
		}
		// Token: 0x0600004C RID: 76 RVA: 0x000092A8 File Offset: 0x000074A8
		// ================== ПУНКТ 2: ПЕРЕМЕЩЕНИЕ СТРОК ПЕРЕТАСКИВАНИЕМ ==================
		// Drag and Drop теперь работает ВО ВСЕХ ТРЁХ окнах списка видео (Рекламная пауза,
		// Перерыв, Таймаут) и в списке сценариев — схема та же, что уже была в
		// «Рекламной паузе»: MouseDown/MouseMove + DoDragDrop с системным порогом DragSize.
		// Для CheckedListBox («Рекламная пауза») оставлен прежний код: он дополнительно
		// сохраняет состояние «галочек», обычному ListBox это не нужно.
		// Порядок фиксируется пунктом «Файл -> Сохранить порядок воспроизведения»
		// (сценарии) и «Файл -> Сохранить список видео» (видео).
		private void Patch_EnableReorderDragDrop(ListBox lb)
		{
			if (lb == null)
			{
				return;
			}
			int[] dragIdx = new int[1];
			bool[] dragging = new bool[1];
			Point[] dragStart = new Point[1];
			dragIdx[0] = -1;
			lb.AllowDrop = true;
			lb.MouseDown += delegate(object sMD, MouseEventArgs eMD)
			{
				if (eMD.Button == MouseButtons.Left)
				{
					dragIdx[0] = lb.IndexFromPoint(eMD.Location);
					dragStart[0] = new Point(eMD.X, eMD.Y);
				}
			};
			lb.MouseMove += delegate(object sMM, MouseEventArgs eMM)
			{
				if (eMM.Button != MouseButtons.Left || dragIdx[0] < 0 || dragging[0])
				{
					return;
				}
				if (Math.Abs(eMM.X - dragStart[0].X) < SystemInformation.DragSize.Width &&
					Math.Abs(eMM.Y - dragStart[0].Y) < SystemInformation.DragSize.Height)
				{
					return;   // движение меньше системного порога перетаскивания
				}
				dragging[0] = true;
				try
				{
					lb.DoDragDrop(lb.Items[dragIdx[0]], DragDropEffects.Move);
				}
				catch (Exception exDrag)
				{
					TabloPatch.Diag.Note("DragDrop " + lb.Name, exDrag);
				}
				dragging[0] = false;
				dragIdx[0] = -1;
			};
			lb.DragOver += delegate(object sDO, DragEventArgs eDO)
			{
				if (dragging[0] && (eDO.AllowedEffect & DragDropEffects.Move) == DragDropEffects.Move)
				{
					eDO.Effect = DragDropEffects.Move;
				}
				else
				{
					eDO.Effect = DragDropEffects.None;
				}
			};
			lb.DragDrop += delegate(object sDD, DragEventArgs eDD)
			{
				try
				{
					if (!dragging[0] || dragIdx[0] < 0)
					{
						return;
					}
					Point pt = lb.PointToClient(new Point(eDD.X, eDD.Y));
					int target = lb.IndexFromPoint(pt);
					if (target < 0)
					{
						target = lb.Items.Count - 1;   // бросили ниже списка — в конец
					}
					int src = dragIdx[0];
					if (target < 0 || target == src)
					{
						return;
					}
					object moved = lb.Items[src];
					lb.BeginUpdate();
					try
					{
						lb.Items.RemoveAt(src);
						if (target > lb.Items.Count)
						{
							target = lb.Items.Count;
						}
						lb.Items.Insert(target, moved);
					}
					finally
					{
						lb.EndUpdate();
					}
					lb.SelectedIndex = target;
					TabloPatch.SerialPortLog.Write("СПИСОК (" + lb.Name + "): строка перемещена перетаскиванием " + (src + 1).ToString() + " -> " + (target + 1).ToString() + ". Не забудьте сохранить порядок.");
				}
				catch (Exception exDrop)
				{
					TabloPatch.Diag.Note("DragDrop перемещение " + lb.Name, exDrop);
				}
			};
		}

		private void Common_but_VideoUp_Click(object sender, EventArgs e, ListBox lb)
		{
			if (lb.SelectedIndex < 0)
			{
				return;
			}
			int num = lb.SelectedIndex - 1;
			if (num > -1)
			{
				lb.Items.Insert(num, lb.SelectedItem);
				lb.Items.RemoveAt(num + 1);   // ПУНКТ 2: было num + 2 - удалялась соседняя строка вместо перемещаемой
				lb.SelectedIndex = num;
			}
		}

		// Token: 0x0600004D RID: 77 RVA: 0x000092F4 File Offset: 0x000074F4
		private void Common_but_VideoDown_Click(object sender, EventArgs e, ListBox lb)
		{
			if (lb.SelectedIndex < 0)
			{
				return;
			}
			int num = lb.SelectedIndex + 1;
			if (num < lb.Items.Count)
			{
				lb.Items.Insert(num + 1, lb.SelectedItem);
				lb.Items.RemoveAt(num - 1);
				lb.SelectedIndex = num;
			}
		}

		// Token: 0x0600004E RID: 78 RVA: 0x0000934C File Offset: 0x0000754C
		private void Common_but_DeleteVideo_Click(object sender, EventArgs e, ListBox lb)
		{
			if (lb.SelectedIndex > -1)
			{
				int num = lb.SelectedIndex;
				lb.Items.RemoveAt(lb.SelectedIndex);
				if (num >= lb.Items.Count)
				{
					num--;
				}
				lb.SelectedIndex = num;
			}
		}

		// Token: 0x0600004F RID: 79 RVA: 0x00009394 File Offset: 0x00007594
		private void Common_VideoFiles_DrawItem(ListBox lb, DrawItemEventArgs e)
		{
			e.DrawBackground();
			if (e.Index > -1)
			{
				SolidBrush brush = new SolidBrush(e.ForeColor);
				e.Graphics.DrawString((e.Index + 1).ToString() + ". " + Path.GetFileName(lb.Items[e.Index].ToString()), e.Font, brush, e.Bounds, StringFormat.GenericDefault);
			}
			e.DrawFocusRectangle();
		}

		// Token: 0x06000050 RID: 80 RVA: 0x00002352 File Offset: 0x00000552
		private void Common_VideoFiles_SelectedIndexChanged(ListBox lb, EventArgs e)
		{
			if (lb.SelectedIndex > -1)
			{
				this.toolStripStatusLabel1.Text = lb.Items[lb.SelectedIndex].ToString();
			}
		}

		// Token: 0x06000051 RID: 81 RVA: 0x0000941C File Offset: 0x0000761C
		private void cLB_ScriptFiles_DrawItem(object sender, DrawItemEventArgs e)
		{
			ListBox listBox = (ListBox)sender;
			e.DrawBackground();
			if (e.Index > -1)
			{
				SolidBrush brush = new SolidBrush(e.ForeColor);
				string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(listBox.Items[e.Index].ToString());
				e.Graphics.DrawString((e.Index + 1).ToString() + ". " + fileNameWithoutExtension, e.Font, brush, e.Bounds, StringFormat.GenericDefault);
			}
			e.DrawFocusRectangle();
			if (this._Controlboard != null && this._Controlboard.ScriptCount != listBox.Items.Count)
			{
				this._Controlboard.ScriptCount = listBox.Items.Count;
			}
		}

		// Token: 0x06000052 RID: 82 RVA: 0x000094E0 File Offset: 0x000076E0
		private void cLB_ScriptFiles_SelectedIndexChanged(object sender, EventArgs e)
		{
			ListBox listBox = (ListBox)sender;
			if (listBox.SelectedIndex > -1)
			{
				this.toolStripStatusLabel1.Text = listBox.Items[listBox.SelectedIndex].ToString();
			}
		}

		// Token: 0x06000053 RID: 83 RVA: 0x00009520 File Offset: 0x00007720
		private void but_AddScript_Click(object sender, EventArgs e)
		{
			// ================== ПУНКТ 2: СПИСОК СЦЕНАРИЕВ И ФОРМАТЫ ФАЙЛОВ ==================
			// БЫЛО: диалог принимал ОДИН файл и только *.sbin, а порядок воспроизведения
			// сохранялся в *.spf; содержимое .spf из этого окна посмотреть было нельзя.
			// СТАЛО:
			//  • несколько файлов за один раз (Multiselect) — как в «Рекламной паузе»;
			//  • фильтр включает и *.sbin, и *.spf;
			//  • если выбран .spf — он ЗАГРУЖАЕТСЯ как список воспроизведения
			//    (содержимое файла становится списком сценариев), а не добавляется строкой.
			OpenFileDialog openFileDialog = new OpenFileDialog();
			openFileDialog.Filter = "Файлы сценариев (*.sbin)|*.sbin|Список воспроизведения (*.spf)|*.spf|Все файлы|*.*";
			openFileDialog.Multiselect = true;
			if (DialogResult.OK != openFileDialog.ShowDialog())
			{
				return;
			}
			int addedSc = 0;
			int loadedPl = 0;
			foreach (string fn in openFileDialog.FileNames)
			{
				if (fn == null || fn.Length == 0)
				{
					continue;
				}
				if (fn.EndsWith(".spf", StringComparison.OrdinalIgnoreCase))
				{
					if (this.Patch_LoadScriptPlaylist(fn))
					{
						loadedPl++;
					}
					continue;
				}
				this.cLB_ScriptFiles.Items.Add(fn);
				addedSc++;
			}
			if (loadedPl > 0)
			{
				TabloPatch.SerialPortLog.Write("СЦЕНАРИИ: через «Доб.» загружен список воспроизведения (.spf), файлов: " + loadedPl.ToString());
			}
			if (addedSc > 0)
			{
				TabloPatch.SerialPortLog.Write("СЦЕНАРИИ: добавлено файлов сценариев: " + addedSc.ToString());
				this.Patch_StatusHint("Добавлено сценариев: " + addedSc.ToString());
			}
		}

		// ПУНКТ 2: загрузка списка воспроизведения (.spf) в список сценариев.
		// Путь запоминается в настройках — именно его использует
		// «Файл -> Сохранить порядок воспроизведения».
		private bool Patch_LoadScriptPlaylist(string path)
		{
			try
			{
				string[] lines = TabloPatch.TextIO.ReadAllLines(path);
				this.cLB_ScriptFiles.Items.Clear();
				int added = 0;
				for (int i = 0; i < lines.Length; i++)
				{
					string t = (lines[i] == null) ? "" : lines[i].Trim();
					if (t.Length == 0)
					{
						continue;
					}
					this.cLB_ScriptFiles.Items.Add(t);
					added++;
				}
				if (this._settingForm != null && this._settingForm.cfg != null)
				{
					this._settingForm.cfg.ScriptPlaylistPath = path;
					this._settingForm.cfg.Save();
				}
				TabloPatch.SerialPortLog.Write("СЦЕНАРИИ: список воспроизведения загружен из " + path + " (позиций: " + added.ToString() + ")");
				this.Patch_StatusHint("Список воспроизведения загружен: " + added.ToString() + " поз.");
				return true;
			}
			catch (Exception exPl)
			{
				TabloPatch.Diag.Note("Загрузка списка воспроизведения .spf", exPl);
				MessageBox.Show("Не удалось загрузить список воспроизведения:\r\n" + exPl.Message, "Табло", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return false;
			}
		}
		// Token: 0x06000054 RID: 84 RVA: 0x00009560 File Offset: 0x00007760
		private void but_ScriptUp_Click(object sender, EventArgs e)
		{
			if (this.cLB_ScriptFiles.SelectedIndex < 0)
			{
				return;
			}
			int num = this.cLB_ScriptFiles.SelectedIndex - 1;
			if (num > -1)
			{
				this.cLB_ScriptFiles.Items.Insert(num, this.cLB_ScriptFiles.SelectedItem);
				this.cLB_ScriptFiles.Items.RemoveAt(num + 1);   // ПУНКТ 2: было num + 2 - удалялась соседняя строка вместо перемещаемой
				this.cLB_ScriptFiles.SelectedIndex = num;
			}
		}

		// Token: 0x06000055 RID: 85 RVA: 0x000095CC File Offset: 0x000077CC
		private void but_ScriptDown_Click(object sender, EventArgs e)
		{
			if (this.cLB_ScriptFiles.SelectedIndex < 0)
			{
				return;
			}
			int num = this.cLB_ScriptFiles.SelectedIndex + 1;
			if (num < this.cLB_ScriptFiles.Items.Count)
			{
				this.cLB_ScriptFiles.Items.Insert(num + 1, this.cLB_ScriptFiles.SelectedItem);
				this.cLB_ScriptFiles.Items.RemoveAt(num - 1);
				this.cLB_ScriptFiles.SelectedIndex = num;
			}
		}

		// Token: 0x06000056 RID: 86 RVA: 0x00009648 File Offset: 0x00007848
		private void but_DeleteScript_Click(object sender, EventArgs e)
		{
			if (this.cLB_ScriptFiles.SelectedIndex > -1)
			{
				int num = this.cLB_ScriptFiles.SelectedIndex;
				this.cLB_ScriptFiles.Items.RemoveAt(this.cLB_ScriptFiles.SelectedIndex);
				if (num >= this.cLB_ScriptFiles.Items.Count)
				{
					num--;
				}
				this.cLB_ScriptFiles.SelectedIndex = num;
			}
		}

		// Token: 0x06000057 RID: 87 RVA: 0x000096B0 File Offset: 0x000078B0
		private void cLB_VideoFiles_DrawItem(object sender, DrawItemEventArgs e)
		{
			ListBox lb = (ListBox)sender;
			this.Common_VideoFiles_DrawItem(lb, e);
		}

		// Token: 0x06000058 RID: 88 RVA: 0x000096CC File Offset: 0x000078CC
		private void cLB_VideoFiles_SelectedIndexChanged(object sender, EventArgs e)
		{
			ListBox lb = (ListBox)sender;
			this.Common_VideoFiles_SelectedIndexChanged(lb, e);
		}

		// Token: 0x06000059 RID: 89 RVA: 0x0000237E File Offset: 0x0000057E
		private void but_AddVideo_Click(object sender, EventArgs e)
		{
			this.Common_but_AddVideo_Click(sender, e, this.cLB_VideoFiles);
		}

		// Token: 0x0600005A RID: 90 RVA: 0x0000238E File Offset: 0x0000058E
		private void but_VideoUp_Click(object sender, EventArgs e)
		{
			this.Common_but_VideoUp_Click(sender, e, this.cLB_VideoFiles);
		}

		// Token: 0x0600005B RID: 91 RVA: 0x0000239E File Offset: 0x0000059E
		private void but_VideoDown_Click(object sender, EventArgs e)
		{
			this.Common_but_VideoDown_Click(sender, e, this.cLB_VideoFiles);
		}

		// Token: 0x0600005C RID: 92 RVA: 0x000023AE File Offset: 0x000005AE
		private void but_DeleteVideo_Click(object sender, EventArgs e)
		{
			this.Common_but_DeleteVideo_Click(sender, e, this.cLB_VideoFiles);
		}

		// Token: 0x0600005D RID: 93 RVA: 0x000096CC File Offset: 0x000078CC
		private void cLB_TimeoutVideoFiles_SelectedIndexChanged(object sender, EventArgs e)
		{
			ListBox lb = (ListBox)sender;
			this.Common_VideoFiles_SelectedIndexChanged(lb, e);
		}

		// Token: 0x0600005E RID: 94 RVA: 0x000096B0 File Offset: 0x000078B0
		private void cLB_TimeoutVideoFiles_DrawItem(object sender, DrawItemEventArgs e)
		{
			ListBox lb = (ListBox)sender;
			this.Common_VideoFiles_DrawItem(lb, e);
		}

		// Token: 0x0600005F RID: 95 RVA: 0x000023BE File Offset: 0x000005BE
		private void but_TimeoutVideoFiles_Add_Click(object sender, EventArgs e)
		{
			this.Common_but_AddVideo_Click(sender, e, this.cLB_TimeoutVideoFiles);
		}

		// Token: 0x06000060 RID: 96 RVA: 0x000023CE File Offset: 0x000005CE
		private void but_TimeoutVideoFiles_Up_Click(object sender, EventArgs e)
		{
			this.Common_but_VideoUp_Click(sender, e, this.cLB_TimeoutVideoFiles);
		}

		// Token: 0x06000061 RID: 97 RVA: 0x000023DE File Offset: 0x000005DE
		private void but_TimeoutVideoFiles_Down_Click(object sender, EventArgs e)
		{
			this.Common_but_VideoDown_Click(sender, e, this.cLB_TimeoutVideoFiles);
		}

		// Token: 0x06000062 RID: 98 RVA: 0x000023EE File Offset: 0x000005EE
		private void but_TimeoutVideoFiles_Delete_Click(object sender, EventArgs e)
		{
			this.Common_but_DeleteVideo_Click(sender, e, this.cLB_TimeoutVideoFiles);
		}

		// Token: 0x06000063 RID: 99 RVA: 0x000023FE File Offset: 0x000005FE
		private void nUD_Score_1_KeyPress(object sender, KeyPressEventArgs e)
		{
			if (e.KeyChar == '\r')
			{
				e.Handled = true;
			}
		}

		// Token: 0x06000064 RID: 100 RVA: 0x000023FE File Offset: 0x000005FE
		private void nUD_Score_2_KeyPress(object sender, KeyPressEventArgs e)
		{
			if (e.KeyChar == '\r')
			{
				e.Handled = true;
			}
		}

		// Token: 0x06000065 RID: 101 RVA: 0x000023FE File Offset: 0x000005FE
		private void nUD_Period_KeyPress(object sender, KeyPressEventArgs e)
		{
			if (e.KeyChar == '\r')
			{
				e.Handled = true;
			}
		}

		// Token: 0x06000066 RID: 102 RVA: 0x000096E8 File Offset: 0x000078E8
		private void dTP_GameTime_ValueChanged(object sender, EventArgs e)
		{
			DateTimePicker dateTimePicker = (DateTimePicker)sender;
			if (this._GameMode == frmMain.gameMode.GameStoped && this._gameTime != TimeSpan.Zero)
			{
				TimeSpan timeSpan = TimeSpan.FromHours((double)dateTimePicker.Value.Hour) + TimeSpan.FromMinutes((double)dateTimePicker.Value.Minute) + TimeSpan.FromSeconds((double)dateTimePicker.Value.Second);
				if (this._settingForm != null)
				{
					if (this._settingForm.cfg.ForwardStrokeGame)
					{
						this._gameTime = this._settingForm.cfg.GameTime - timeSpan;
					}
					else
					{
						this._gameTime = timeSpan;
					}
				}
				else
				{
					this._gameTime = timeSpan;
				}
				if (this.gs_CentreWindow != null)
				{
					this.gs_CentreWindow.Text = FmtClock(timeSpan, false);
				}
			}
		}

		// Token: 0x06000067 RID: 103 RVA: 0x000097F4 File Offset: 0x000079F4
		private void _Controlboard_OnCommandSetCount(object sender, CommandEventArgs e)
		{
						if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandSetCount), new object[] { sender, e });
				return;
			}
			TCommandSetCount tcommandSetCount = (TCommandSetCount)e.DP;
			if (tcommandSetCount.SetCount_A)
			{
				this.nUD_Score_1.Value = tcommandSetCount.Count_A;
			}
			if (tcommandSetCount.SetCount_B)
			{
				this.nUD_Score_2.Value = tcommandSetCount.Count_B;
			}
		}

		// Token: 0x06000068 RID: 104 RVA: 0x0000984C File Offset: 0x00007A4C
		private void _Controlboard_OnCommandResetMatch(object sender, CommandEventArgs e)
		{
						if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandResetMatch), new object[] { sender, e });
				return;
			}
			TCommandResetMatch tcommandResetMatch = (TCommandResetMatch)e.DP;
			if (tcommandResetMatch.ResetMatch)
			{
				this.Cmd_ResetGame();
			}
			if (tcommandResetMatch.ResetGameTime)
			{
				this.Cmd_TimeReset();
			}
			if (tcommandResetMatch.SetPeriod)
			{
				this.nUD_Period.Value = tcommandResetMatch.Period;
			}
		}

		// Token: 0x06000069 RID: 105 RVA: 0x000098A0 File Offset: 0x00007AA0
		private void _Controlboard_OnCommandSetCounterGame(object sender, CommandEventArgs e)
		{
			if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandSetCounterGame), new object[] { sender, e });
				return;
			}
			TCommandSetCounterGame tcommandSetCounterGame = (TCommandSetCounterGame)e.DP;
			if (tcommandSetCounterGame.SetCounterGame && this._GameMode != frmMain.gameMode.GameStarted && this._gameTime != TimeSpan.Zero)
			{
				this._gameTime = TimeSpan.FromSeconds((double)tcommandSetCounterGame.Time);
			}
		}

		// Token: 0x0600006A RID: 106 RVA: 0x000098F0 File Offset: 0x00007AF0
				private void _Controlboard_OnCommandSetTimerGame(object sender, CommandEventArgs e)
		{
						if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandSetTimerGame), new object[] { sender, e });
				return;
			}
			TCommandSetTimerGame tcommandSetTimerGame = (TCommandSetTimerGame)e.DP;
			if (this._settingForm == null)
			{
				return;
			}
			TabloPatch.SerialPortLog.Write("ПУЛЬТ SetTimerGame: время=" + (int)tcommandSetTimerGame.Time + " Direction=" + tcommandSetTimerGame.Direction + " AutoWhistle=" + tcommandSetTimerGame.EnableAutoWhistle);
			this.Patch_RemoteSetTimer((int)tcommandSetTimerGame.Time, !tcommandSetTimerGame.Direction, tcommandSetTimerGame.EnableAutoWhistle);
		}

		// Token: 0x0600006B RID: 107 RVA: 0x000099D8 File Offset: 0x00007BD8
		private void _Controlboard_OnCommandSetBreak(object sender, CommandEventArgs e)
		{
			if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandSetBreak), new object[] { sender, e });
				return;
			}
			TCommandBreak tcommandBreak = (TCommandBreak)e.DP;
			if (this._settingForm == null)
			{
				return;
			}
			this._settingForm.cfg.BreakTime = TimeSpan.FromSeconds((double)tcommandBreak.Time);
			this._settingForm.cfg.Save();
		}

		// Token: 0x0600006C RID: 108 RVA: 0x00009A28 File Offset: 0x00007C28
		private void _Controlboard_OnCommandSetTimeout(object sender, CommandEventArgs e)
		{
		if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandSetTimeout), new object[] { sender, e });
				return;
			}
			TCommandTimeout tcommandTimeout = (TCommandTimeout)e.DP;
			if (this._settingForm == null)
			{
				return;
			}
			this._settingForm.cfg.TimeoutTime = TimeSpan.FromSeconds((double)tcommandTimeout.Time);
			this._settingForm.cfg.Save();
		}

		// Token: 0x0600006D RID: 109 RVA: 0x00009A78 File Offset: 0x00007C78
		private void _Controlboard_OnCommandSetWhistle(object sender, CommandEventArgs e)
		{
						if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandSetWhistle), new object[] { sender, e });
				return;
			}
			TCommandWhistle tcommandWhistle = (TCommandWhistle)e.DP;
			if (tcommandWhistle.ChangeModeGame)
			{
				if (tcommandWhistle.Enable)
				{
					this.Cmd_Start();
				}
				else
				{
					this.Cmd_Stop();
				}
			}
			if (tcommandWhistle.PlayWhistle)
			{
				this.WhistleBeep();
			}
		}

		// Token: 0x0600006E RID: 110 RVA: 0x00009AC0 File Offset: 0x00007CC0
		private void _Controlboard_OnCommandSetScript(object sender, CommandEventArgs e)
		{
		if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandSetScript), new object[] { sender, e });
				return;
			}
			TCommandScript tcommandScript = (TCommandScript)e.DP;
			if (!tcommandScript.ShowMode)
			{
				this.Cmd_ShowPrompt();
			}
			else
			{
				this.Cmd_ShowPrompt();
			}
			if (tcommandScript.PlayScript)
			{
				if (tcommandScript.ScriptNumber == 0)
				{
					this.Cmd_ScriptStop();
					return;
				}
				this.cLB_ScriptFiles.SelectedIndex = tcommandScript.ScriptNumber - 1;
				this.Cmd_ScriptPlay();
			}
		}

		// Token: 0x0600006F RID: 111 RVA: 0x00009B20 File Offset: 0x00007D20
						private void _Controlboard_OnCommandDelGamer(object sender, CommandEventArgs e)
		{
						if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandDelGamer), new object[] { sender, e });
				return;
			}
			TCommandDelGamer tcommandDelGamer = (TCommandDelGamer)e.DP;
			int playerNumber = (int)tcommandDelGamer.GamerNumber;
			int sec = (int)tcommandDelGamer.Time;
			TabloPatch.SerialPortLog.Write("ПУЛЬТ DelGamer: номер=" + playerNumber + " время=" + sec + " PlayedCommand=" + tcommandDelGamer.PlayedCommand);
			// БЛОК 4, F5-06/F6-10: валидация данных пульта — номер вне 1..99 логируется
			// и игнорируется (защита от «мусора в линии»). Протокол пульта НЕ изменён (Q5).
			if (playerNumber < 1 || playerNumber > 99)
			{
				TabloPatch.SerialPortLog.Write("ПУЛЬТ DelGamer: ОТКЛОНЕНО — номер " + playerNumber + " вне диапазона 1..99");
				return;
			}
			if (sec < 0)
			{
				sec = 0;
			}
			ListBox lb = tcommandDelGamer.PlayedCommand ? this.lB_DelPeople_B : this.lB_DelPeople_A;
						if (sec <= 0)
			{
				if (this.Patch_PlayerInList(lb, playerNumber))
				{
					this.Patch_RemoveFromList(lb, playerNumber);
				}
				else
				{
					this.Add_del(lb, playerNumber.ToString("D2"));
				}
				return;
			}
			TimeSpan timeSpan = TimeSpan.FromSeconds((double)sec);
			string s = playerNumber.ToString("D2") + " " + timeSpan.Minutes.ToString("D2") + ":" + timeSpan.Seconds.ToString("D2");
			this.Add_del(lb, s);
		}
					private void Patch_PurgeExpired()
		{
			for (int i = this.lB_DelPeople_A.Items.Count - 1; i >= 0; i--)
			{
				string text = this.lB_DelPeople_A.Items[i] as string;
				if (text != null && text.Length >= 8 && text.Substring(3, 5) == "00:00")
				{
					this.lB_DelPeople_A.Items.RemoveAt(i);
				}
			}
			for (int j = this.lB_DelPeople_B.Items.Count - 1; j >= 0; j--)
			{
				string text2 = this.lB_DelPeople_B.Items[j] as string;
				if (text2 != null && text2.Length >= 8 && text2.Substring(3, 5) == "00:00")
				{
					this.lB_DelPeople_B.Items.RemoveAt(j);
				}
			}
			this.ShowPenalty(0, this.lB_DelPeople_A, this.gs_LWindow_2);
			this.ShowPenalty(1, this.lB_DelPeople_A, this.gs_LWindow_3);
			this.ShowPenalty(0, this.lB_DelPeople_B, this.gs_RWindow_2);
			this.ShowPenalty(1, this.lB_DelPeople_B, this.gs_RWindow_3);
			this.TabloMode();
		}
		private bool Patch_PlayerInList(ListBox lb, int playerNumber)
		{
			string prefix = playerNumber.ToString("D2");
			foreach (object obj in lb.Items)
			{
				string text = obj as string;
				if (text != null && text.Length >= 2 && text.Substring(0, 2) == prefix)
				{
					return true;
				}
			}
			return false;
		}

		private void Patch_RemoveFromList(ListBox lb, int playerNumber)
		{
			string prefix = playerNumber.ToString("D2");
			for (int i = lb.Items.Count - 1; i >= 0; i--)
			{
				string text = lb.Items[i] as string;
				if (text != null && text.Length >= 2 && text.Substring(0, 2) == prefix)
				{
					lb.Items.RemoveAt(i);
				}
			}
			this.ShowPenalty(0, this.lB_DelPeople_A, this.gs_LWindow_2);
			this.ShowPenalty(1, this.lB_DelPeople_A, this.gs_LWindow_3);
			this.ShowPenalty(0, this.lB_DelPeople_B, this.gs_RWindow_2);
			this.ShowPenalty(1, this.lB_DelPeople_B, this.gs_RWindow_3);
			this.TabloMode();
			this.statusShow();
			TabloPatch.SerialPortLog.Write("УДАЛЕНИЕ СНЯТО: игрок №" + playerNumber + " убран из списка своей команды");
		}

		// Token: 0x06000070 RID: 112 RVA: 0x00009BCC File Offset: 0x00007DCC
		private void _Controlboard_OnCommandTimeoutCounters(object sender, CommandEventArgs e)
		{
						if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandTimeoutCounters), new object[] { sender, e });
				return;
			}
			TCommandTimeoutCounters tcommandTimeoutCounters = (TCommandTimeoutCounters)e.DP;
			if (tcommandTimeoutCounters.Enable)
			{
				if (this._settingForm != null)
				{
					this._settingForm.cfg.TimeoutVideoEnable = tcommandTimeoutCounters.ShowVideo;
					this._settingForm.cfg.Save();
				}
				// БЛОК 4, F6-05: изменение счётчиков тайм-аутов с пульта фиксируется в логе
				TabloPatch.SerialPortLog.Write("СЧЁТЧИКИ ТАЙМ-АУТОВ (пульт): дома=" + (int)((ushort)tcommandTimeoutCounters.Count_A) + " гости=" + (int)((ushort)tcommandTimeoutCounters.Count_B) + " видеоТаймаут=" + tcommandTimeoutCounters.ShowVideo);
				this.Cmd_Timeout((int)((ushort)tcommandTimeoutCounters.Count_A), (int)((ushort)tcommandTimeoutCounters.Count_B), false);
			}
		}

		// Token: 0x06000071 RID: 113 RVA: 0x00009C30 File Offset: 0x00007E30
		private void _Controlboard_OnCommandSetBreakMode(object sender, CommandEventArgs e)
		{
			if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandSetBreakMode), new object[] { sender, e });
				return;
			}
			TCommandSetBreakMode tcommandSetBreakMode = (TCommandSetBreakMode)e.DP;
			if (this._settingForm != null)
			{
				this._settingForm.cfg.BreakVideoEnable = tcommandSetBreakMode.ShowVideo;
				this._settingForm.cfg.Save();
			}
			this.Cmd_BreakGame();
		}

		// Token: 0x06000072 RID: 114 RVA: 0x00009C80 File Offset: 0x00007E80
		private void _Controlboard_OnCommandSetName_A(object sender, CommandEventArgs e)
		{
			if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandSetName_A), new object[] { sender, e });
				return;
			}
			TCommandSetName tcommandSetName = (TCommandSetName)e.DP;
			this.Patch_CheckPultText(tcommandSetName.Name, "SetName_A");   // БЛОК 4, F5-08
			this.mtb_Command_A.Text = tcommandSetName.Name;
		}

		// Token: 0x06000073 RID: 115 RVA: 0x00009CAC File Offset: 0x00007EAC
		private void _Controlboard_OnCommandSetName_B(object sender, CommandEventArgs e)
		{
			if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandSetName_B), new object[] { sender, e });
				return;
			}
			TCommandSetName tcommandSetName = (TCommandSetName)e.DP;
			this.Patch_CheckPultText(tcommandSetName.Name, "SetName_B");   // БЛОК 4, F5-08
			this.mtb_Command_B.Text = tcommandSetName.Name;
		}

		// Token: 0x06000074 RID: 116 RVA: 0x00009CD8 File Offset: 0x00007ED8
		private void _Controlboard_OnCommandSetTownName_A(object sender, CommandEventArgs e)
		{
		
			if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandSetTownName_A), new object[] { sender, e });
				return;
			}
			TCommandSetName tcommandSetName = (TCommandSetName)e.DP;
			this.Patch_CheckPultText(tcommandSetName.Name, "SetTownName_A");   // БЛОК 4, F5-08
			this.mtb_TownCommand_A.Text = tcommandSetName.Name;
		}

		// Token: 0x06000075 RID: 117 RVA: 0x00009D04 File Offset: 0x00007F04
		private void _Controlboard_OnCommandSetTownName_B(object sender, CommandEventArgs e)
		{
					
			if (this.InvokeRequired)
			{
				this.BeginInvoke(new Action<object, CommandEventArgs>(this._Controlboard_OnCommandSetTownName_B), new object[] { sender, e });
				return;
			}
			TCommandSetName tcommandSetName = (TCommandSetName)e.DP;
			this.Patch_CheckPultText(tcommandSetName.Name, "SetTownName_B");   // БЛОК 4, F5-08
			this.mtb_TownCommand_B.Text = tcommandSetName.Name;
		}

		// Token: 0x06000076 RID: 118 RVA: 0x00009D30 File Offset: 0x00007F30
		private void vpf_OnVideoPlayerStop(object sender, EventArgs e)
		{
			this.VideoPlayed = true;
			this.CurVideoPosition = 0.0;
			try
			{
				if (this._NowPlayList != null && this._NowPlayList.Items.Count > 0)
				{
					this.PrepareVideoFile(this._NowPlayList);
				}
			}
			catch
			{
			}
		}

		// Token: 0x06000077 RID: 119 RVA: 0x00009D90 File Offset: 0x00007F90
		private void vpf_OnVideoPlayerLoadFile(object sender, EventArgs e)
		{
			try
			{
				this.vpf.Show();
				this.vpf.Play(this.CurVideoPosition);
				if (this.thf != null)
				{
					this.CloseTabloHockey();
				}
			}
			catch
			{
			}
		}

		// Token: 0x06000078 RID: 120 RVA: 0x00009DDC File Offset: 0x00007FDC
		private void gs_Main_OnAnimatedStop(object sender, EventArgs e)
		{
			GraphString graphString = (GraphString)sender;
			if (graphString.ShowTablo4String)
			{
				graphString.TwoLines = false;
				graphString.ShowTablo4String = false;
			}
			graphString.TextRefrash();
			this._timerRestore.Start();
		}

		// Token: 0x06000079 RID: 121 RVA: 0x00009E18 File Offset: 0x00008018
		private void CreateBackgroundWindow(int width, int height)
		{
			if (this.thfBackground == null)
			{
				this.thfBackground = new Form();
				this.thfBackground.FormBorderStyle = FormBorderStyle.None;
				this.thfBackground.TopMost = this._settingForm.cfg.TopWindow;
				this.thfBackground.ControlBox = false;
				this.thfBackground.ShowInTaskbar = false;
				this.thfBackground.StartPosition = FormStartPosition.Manual;
			}
			this.thfBackground.BackColor = this._settingForm.cfg.BackColor;
			this.thfBackground.Left = this._settingForm.cfg.Left;
			this.thfBackground.Top = this._settingForm.cfg.Top;
			this.thfBackground.Width = width;
			this.thfBackground.Height = height;
			this.thfBackground.Show();
			base.Invalidate();
		}

		// Token: 0x0600007A RID: 122 RVA: 0x00002411 File Offset: 0x00000611
		private void CloseBackgroundWindow()
		{
			if (this.thfBackground != null)
			{
				this.thfBackground.Close();
				this.thfBackground = null;
			}
		}

		// Token: 0x0600007B RID: 123 RVA: 0x00009F00 File Offset: 0x00008100
		private void CreateGraphString()
		{
			if (this.FONT_DATA_Main == null)
			{
				this.FONT_DATA_Main = new byte[FontDataSize];
				this.FontLoad(Patch_ResolveFontPath(), ref this.FONT_DATA_Main);
				//Patch_TrySynthesizeYo();   // БЛОК 1c (Q4a): синтез не сработал на реальном полотне - вместо него замена Ё->Е в Patch_BoardText
			}
			this.gs_Main = new GraphString(string.Empty, 224, 36, 16, this.FONT_DATA_Main);
			this.gs_Main.AnimatedString = false;
			this.gs_Main.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_Main.FontColor = this._settingForm.cfg.FontColor;
			this.gs_Main.Speed = 10U;
			this.gs_Main.fps = 60;
			this.gs_Main.Top = 8;
			this.gs_Main.Left = 8;
			this.gs_Main.Stretch = true;
			this.gs_Main.OnAnimatedStop += this.gs_Main_OnAnimatedStop;
			this.gs_Main.ParentForm = this;
			this.gs_LWindow_1 = new GraphString(((int)this.nUD_Score_1.Value).ToString(), 48, 32, 16, this.FONT_DATA_Main);
			this.gs_LWindow_1.AnimatedString = false;
			this.gs_LWindow_1.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_LWindow_1.FontColor = this._settingForm.cfg.FontColor;
			this.gs_LWindow_1.Speed = 10U;
			this.gs_LWindow_1.fps = 60;
			this.gs_LWindow_1.Top = 52;
			this.gs_LWindow_1.Left = 8;
			this.gs_LWindow_1.Stretch = true;
			this.gs_LWindow_1.Align = StringAlign.csCentre;
			this.gs_LWindow_2 = new GraphString(string.Empty, 87, 16, 16, this.FONT_DATA_Main);
			this.gs_LWindow_2.AnimatedString = false;
			this.gs_LWindow_2.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_LWindow_2.FontColor = this._settingForm.cfg.FontColor;
			this.gs_LWindow_2.Speed = 10U;
			this.gs_LWindow_2.fps = 60;
			this.gs_LWindow_2.Top = 112;
			this.gs_LWindow_2.Left = 8;
			this.gs_LWindow_2.Stretch = true;
			this.gs_LWindow_2.Align = StringAlign.csCentre;
			this.gs_LWindow_3 = new GraphString(string.Empty, 87, 16, 16, this.FONT_DATA_Main);
			this.gs_LWindow_3.AnimatedString = false;
			this.gs_LWindow_3.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_LWindow_3.FontColor = this._settingForm.cfg.FontColor;
			this.gs_LWindow_3.Speed = 10U;
			this.gs_LWindow_3.fps = 60;
			this.gs_LWindow_3.Top = 136;
			this.gs_LWindow_3.Left = 8;
			this.gs_LWindow_3.Stretch = true;
			this.gs_LWindow_3.Align = StringAlign.csCentre;
			this.gs_CentreWindow = new GraphString("00:00", 104, 32, 16, this.FONT_DATA_Main);
			this.gs_CentreWindow.AnimatedString = false;
			this.gs_CentreWindow.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_CentreWindow.FontColor = this._settingForm.cfg.FontColor;
			this.gs_CentreWindow.Speed = 10U;
			this.gs_CentreWindow.fps = 60;
			this.gs_CentreWindow.Top = 52;
			this.gs_CentreWindow.Left = 68;
			this.gs_CentreWindow.Stretch = true;
			this.gs_CentreWindow.Align = StringAlign.csCentre;
			this.gs_CentreWindow.Visible = true;
			this.gs_CentreWindow_small = new GraphString(((int)this.nUD_Period.Value).ToString(), 24, 24, 16, this.FONT_DATA_Main);
			this.gs_CentreWindow_small.AnimatedString = false;
			this.gs_CentreWindow_small.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_CentreWindow_small.FontColor = this._settingForm.cfg.FontColor;
			this.gs_CentreWindow_small.Speed = 10U;
			this.gs_CentreWindow_small.fps = 60;
			this.gs_CentreWindow_small.Top = 96;
			this.gs_CentreWindow_small.Left = 108;
			this.gs_CentreWindow_small.Stretch = true;
			this.gs_CentreWindow_small.Align = StringAlign.csCentre;
			this.gs_RWindow_1 = new GraphString(((int)this.nUD_Score_1.Value).ToString(), 48, 32, 16, this.FONT_DATA_Main);
			this.gs_RWindow_1.AnimatedString = false;
			this.gs_RWindow_1.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_RWindow_1.FontColor = this._settingForm.cfg.FontColor;
			this.gs_RWindow_1.Speed = 10U;
			this.gs_RWindow_1.fps = 60;
			this.gs_RWindow_1.Top = 52;
			this.gs_RWindow_1.Left = 184;
			this.gs_RWindow_1.Stretch = true;
			this.gs_RWindow_1.Align = StringAlign.csCentre;
			this.gs_RWindow_2 = new GraphString(string.Empty, 87, 16, 16, this.FONT_DATA_Main);
			this.gs_RWindow_2.AnimatedString = false;
			this.gs_RWindow_2.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_RWindow_2.FontColor = this._settingForm.cfg.FontColor;
			this.gs_RWindow_2.Speed = 10U;
			this.gs_RWindow_2.fps = 60;
			this.gs_RWindow_2.Top = 112;
			this.gs_RWindow_2.Left = 145;
			this.gs_RWindow_2.Stretch = true;
			this.gs_RWindow_2.Align = StringAlign.csCentre;
			this.gs_RWindow_3 = new GraphString(string.Empty, 87, 16, 16, this.FONT_DATA_Main);
			this.gs_RWindow_3.AnimatedString = false;
			this.gs_RWindow_3.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_RWindow_3.FontColor = this._settingForm.cfg.FontColor;
			this.gs_RWindow_3.Speed = 10U;
			this.gs_RWindow_3.fps = 60;
			this.gs_RWindow_3.Top = 136;
			this.gs_RWindow_3.Left = 145;
			this.gs_RWindow_3.Stretch = true;
			this.gs_RWindow_3.Align = StringAlign.csCentre;
			this.gs_Clock = new GraphString(string.Empty, 230, 40, 16, this.FONT_DATA_Main);
			this.gs_Clock.AnimatedString = false;
			this.gs_Clock.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_Clock.FontColor = this._settingForm.cfg.PromptColor;
			this.gs_Clock.Speed = 10U;
			this.gs_Clock.fps = 60;
			this.gs_Clock.Top = 10;
			this.gs_Clock.Left = 5;
			this.gs_Clock.Stretch = true;
			this.gs_Clock.Align = StringAlign.csCentre;
			this.gs_Date = new GraphString(string.Empty, 210, 16, 16, this.FONT_DATA_Main);
			this.gs_Date.AnimatedString = false;
			this.gs_Date.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_Date.FontColor = this._settingForm.cfg.PromptColor;
			this.gs_Date.Speed = 10U;
			this.gs_Date.fps = 60;
			this.gs_Date.Top = 60;
			this.gs_Date.Left = 10;
			this.gs_Date.Stretch = true;
			this.gs_Date.Align = StringAlign.csRight;
			this.gs_BeforeGameTime = new GraphString(string.Empty, 230, 32, 16, this.FONT_DATA_Main);
			this.gs_BeforeGameTime.AnimatedString = false;
			this.gs_BeforeGameTime.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_BeforeGameTime.FontColor = this._settingForm.cfg.BeforeStartTextColor;
			this.gs_BeforeGameTime.Speed = 10U;
			this.gs_BeforeGameTime.fps = 60;
			this.gs_BeforeGameTime.Top = 120;
			this.gs_BeforeGameTime.Left = 5;
			this.gs_BeforeGameTime.Stretch = true;
			this.gs_BeforeGameTime.Align = StringAlign.csCentre;
		}

		// Token: 0x0600007C RID: 124 RVA: 0x0000A7AC File Offset: 0x000089AC
		private void ChangeSettingGraphString()
		{
			this.Patch_SetFontDataOnce(this.gs_Main);   // БЛОК 4, F9-09
			this.gs_Main.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_Main.FontColor = this._settingForm.cfg.FontColor;
			this.Patch_SetFontDataOnce(this.gs_LWindow_1);   // БЛОК 4, F9-09
			this.gs_LWindow_1.AnimatedString = false;
			this.gs_LWindow_1.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_LWindow_1.FontColor = this._settingForm.cfg.FontColor;
			this.Patch_SetFontDataOnce(this.gs_LWindow_2);   // БЛОК 4, F9-09
			this.gs_LWindow_2.AnimatedString = false;
			this.gs_LWindow_2.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_LWindow_2.FontColor = this._settingForm.cfg.FontColor;
			this.Patch_SetFontDataOnce(this.gs_LWindow_3);   // БЛОК 4, F9-09
			this.gs_LWindow_3.AnimatedString = false;
			this.gs_LWindow_3.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_LWindow_3.FontColor = this._settingForm.cfg.FontColor;
			this.Patch_SetFontDataOnce(this.gs_CentreWindow);   // БЛОК 4, F9-09
			this.gs_CentreWindow.AnimatedString = false;
			this.gs_CentreWindow.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_CentreWindow.FontColor = this._settingForm.cfg.FontColor;
			this.Patch_SetFontDataOnce(this.gs_CentreWindow_small);   // БЛОК 4, F9-09
			this.gs_CentreWindow_small.AnimatedString = false;
			this.gs_CentreWindow_small.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_CentreWindow_small.FontColor = this._settingForm.cfg.FontColor;
			this.Patch_SetFontDataOnce(this.gs_RWindow_1);   // БЛОК 4, F9-09
			this.gs_RWindow_1.AnimatedString = false;
			this.gs_RWindow_1.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_RWindow_1.FontColor = this._settingForm.cfg.FontColor;
			this.Patch_SetFontDataOnce(this.gs_RWindow_2);   // БЛОК 4, F9-09
			this.gs_RWindow_2.AnimatedString = false;
			this.gs_RWindow_2.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_RWindow_2.FontColor = this._settingForm.cfg.FontColor;
			this.Patch_SetFontDataOnce(this.gs_RWindow_3);   // БЛОК 4, F9-09
			this.gs_RWindow_3.AnimatedString = false;
			this.gs_RWindow_3.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_RWindow_3.FontColor = this._settingForm.cfg.FontColor;
			this.Patch_SetFontDataOnce(this.gs_Clock);   // БЛОК 4, F9-09
			this.gs_Clock.AnimatedString = false;
			this.gs_Clock.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_Clock.FontColor = this._settingForm.cfg.PromptColor;
			this.Patch_SetFontDataOnce(this.gs_Date);   // БЛОК 4, F9-09
			this.gs_Date.AnimatedString = false;
			this.gs_Date.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_Date.FontColor = this._settingForm.cfg.PromptColor;
			this.Patch_SetFontDataOnce(this.gs_BeforeGameTime);   // БЛОК 4, F9-09
			this.gs_BeforeGameTime.BackgroundColor = this._settingForm.cfg.BackColor;
			this.gs_BeforeGameTime.FontColor = this._settingForm.cfg.BeforeStartTextColor;
		}

		// Token: 0x0600007D RID: 125 RVA: 0x0000AB88 File Offset: 0x00008D88
		private void OpenTabloHockey()
		{
			if (this.thf == null)
			{
				if (this.thfPrompt != null)
				{
					this.CloseTabloHockeyPrompt();
				}
				if (this.vpf != null)
				{
					this.CloseVideoPlayer();
				}
				this.thf = new TabloHockeyForm();
				this.thf.FormBorderStyle = FormBorderStyle.None;
				this.thf.TopMost = this._settingForm.cfg.TopWindow;
				this.thf.ControlBox = false;
				this.thf.ShowInTaskbar = false;
				this.thf.StartPosition = FormStartPosition.Manual;
				this.thf.Left = this._settingForm.cfg.Left;
				this.thf.Top = this._settingForm.cfg.Top;
				this.thf.BackColor = this._settingForm.cfg.BackColor;
				this.thf.Width = 240;
				this.thf.Height = 160;
				this.thf.fps = 60;
				this.thf.TimeoutColor = Color.White;
				this.CreateBackgroundWindow(this.thf.Width, this.thf.Height);
				if (this.gs_Main != null)
				{
					this.thf.RegistrString(this.gs_Main);
					this.gs_Main.TextRefrash();
				}
				if (this.gs_LWindow_1 != null)
				{
					this.thf.RegistrString(this.gs_LWindow_1);
					this.gs_LWindow_1.TextRefrash();
				}
				if (this.gs_LWindow_2 != null)
				{
					this.thf.RegistrString(this.gs_LWindow_2);
					this.gs_LWindow_2.TextRefrash();
				}
				if (this.gs_LWindow_3 != null)
				{
					this.thf.RegistrString(this.gs_LWindow_3);
					this.gs_LWindow_3.TextRefrash();
				}
				if (this.gs_CentreWindow != null && this.gs_CentreWindow.Visible)
				{
					this.thf.RegistrString(this.gs_CentreWindow);
					this.gs_CentreWindow.TextRefrash();
				}
				if (this.gs_CentreWindow_small != null)
				{
					this.thf.RegistrString(this.gs_CentreWindow_small);
					this.gs_CentreWindow_small.TextRefrash();
				}
				if (this.gs_RWindow_1 != null)
				{
					this.thf.RegistrString(this.gs_RWindow_1);
					this.gs_RWindow_1.TextRefrash();
				}
				if (this.gs_RWindow_2 != null)
				{
					this.thf.RegistrString(this.gs_RWindow_2);
					this.gs_RWindow_2.TextRefrash();
				}
				if (this.gs_RWindow_3 != null)
				{
					this.thf.RegistrString(this.gs_RWindow_3);
					this.gs_RWindow_3.TextRefrash();
				}
				this.thf.TimeOutCount_L = (ushort)this.TimeOutCounter_L;
				this.thf.TimeOutCount_R = (ushort)this.TimeOutCounter_R;
				this.thf.Show();
				this.thf.Start();
				base.Invalidate();
				// БЛОК 4, F8-09: GC.Collect() удалён (фриз UI при открытии окна табло)
			}
		}

		// Token: 0x0600007E RID: 126 RVA: 0x0000AE5C File Offset: 0x0000905C
		private void OpenPromptHockey()
		{
			if (this.thfPrompt == null)
			{
				if (this.thf != null)
				{
					this.CloseTabloHockey();
				}
				if (this.vpf != null)
				{
					this.CloseVideoPlayer();
				}
				this.thfPrompt = new TabloHockeyForm();
				this.thfPrompt.FormBorderStyle = FormBorderStyle.None;
				this.thfPrompt.TopMost = this._settingForm.cfg.TopWindow;
				this.thfPrompt.ControlBox = false;
				this.thfPrompt.ShowInTaskbar = false;
				this.thfPrompt.StartPosition = FormStartPosition.Manual;
				this.thfPrompt.BackColor = this._settingForm.cfg.BackColor;
				this.thfPrompt.Left = this._settingForm.cfg.Left;
				this.thfPrompt.Top = this._settingForm.cfg.Top;
				this.thfPrompt.Width = 240;
				this.thfPrompt.Height = 160;
				this.thfPrompt.fps = 60;
				this.CreateBackgroundWindow(this.thfPrompt.Width, this.thfPrompt.Height);
				if (this.gs_Clock != null)
				{
					this.thfPrompt.RegistrString(this.gs_Clock);
					this.gs_Clock.TextRefrash();
				}
				if (this.gs_Date != null)
				{
					this.thfPrompt.RegistrString(this.gs_Date);
					this.gs_Date.TextRefrash();
				}
				if (this.gs_BeforeGameTime != null)
				{
					this.thfPrompt.RegistrString(this.gs_BeforeGameTime);
					this.gs_BeforeGameTime.TextRefrash();
				}
				this.thfPrompt.Show();
				this.thfPrompt.Start();
				base.Invalidate();
				// БЛОК 4, F8-09: GC.Collect() удалён (фриз UI при открытии заставки)
				string text = DateTime.Now.ToString("HH:mm:ss");
				string text2 = DateTime.Now.ToString("dd MMMM") + " /" + ((frmMain.dayOfWeek)DateTime.Now.DayOfWeek).ToString() + "/";
				if (this.gs_Clock != null)
				{
					this.gs_Clock.Text = text;
				}
				if (this.gs_Date != null)
				{
					this.gs_Date.Text = text2;
				}
				if (this._promptTimer == null)
				{
					this._promptTimer = new Multimedia.Timer();
					this._promptTimer.Period = 500;
					this._promptTimer.Resolution = 100;
					this._promptTimer.Mode = TimerMode.Periodic;
					this._promptTimer.SynchronizingObject = this;
					this._promptTimer.Tick += this._promptTimer_Tick;
				}
				this._promptTimer.Start();
			}
		}

		// Token: 0x0600007F RID: 127 RVA: 0x0000242D File Offset: 0x0000062D
		private void CloseTabloHockey()
		{
			if (this.thf != null)
			{
				this.thf.Close();
				this.thf = null;
			}
		}

		// Token: 0x06000080 RID: 128 RVA: 0x00002449 File Offset: 0x00000649
		private void CloseTabloHockeyPrompt()
		{
			if (this.thfPrompt != null)
			{
				if (this._promptTimer != null)
				{
					this._promptTimer.Stop();
				}
				this.thfPrompt.Close();
				this.thfPrompt = null;
			}
		}

		// Token: 0x06000081 RID: 129 RVA: 0x0000B0F0 File Offset: 0x000092F0
		private const int FontDataSize = 8448;   // 256 слотов * 32 байта + 256 байт таблицы высот
		// F4-05/F4-01: загрузка шрифта с проверкой размера (иначе LoadChar читает вне границ -> падение рендера)
		private bool FontLoad(string font_path, ref byte[] FONT_DATA)
		{
			try
			{
				if (string.IsNullOrEmpty(font_path) || !File.Exists(font_path))
				{
					MessageBox.Show("Файл шрифта не найден:\r\n" + (font_path == null ? "(не задан)" : font_path) + "\r\n\r\nСкопируйте 16fs_new.fdat в папку программы или укажите путь в Настройках.", "Табло — шрифт", MessageBoxButtons.OK, MessageBoxIcon.Warning);
					return false;
				}
				byte[] raw = File.ReadAllBytes(font_path);
				if (raw.Length != FontDataSize)
				{
					MessageBox.Show("Файл шрифта имеет размер " + raw.Length + " байт, а требуется ровно " + FontDataSize + " байт.\r\n" + font_path + "\r\n\r\nИспользовать его НЕЛЬЗЯ (возможно аварийное завершение во время матча). Будет загружен штатный 16fs_new.fdat.", "Табло — файл шрифта повреждён", MessageBoxButtons.OK, MessageBoxIcon.Error);
					TabloPatch.CrashLog.Append("ШРИФТ", "размер " + raw.Length + " != " + FontDataSize + " (" + font_path + ")");   // БЛОК 4, F8-06
					string def = Path.Combine(Application.StartupPath, "16fs_new.fdat");
					if (!File.Exists(def) || new FileInfo(def).Length != FontDataSize) return false;
					raw = File.ReadAllBytes(def);
				}
				// санация таблицы высот (последние 256 байт): высота глифа не должна превышать 16
				for (int i = FontDataSize - 256; i < FontDataSize; i++)
					if (raw[i] > 16) raw[i] = 16;
				FONT_DATA = raw;
				return true;
			}
			catch (Exception ex)
			{
				MessageBox.Show("Не удалось прочитать файл шрифта:\r\n" + font_path + "\r\n" + ex.Message, "Табло — шрифт", MessageBoxButtons.OK, MessageBoxIcon.Error);
				TabloPatch.CrashLog.Append("ШРИФТ", ex.ToString());   // БЛОК 4, F8-06
				return false;
			}
		}

		// F4-01: единая точка разрешения пути шрифта (настройка -> папка программы -> штатный файл)
		private string Patch_ResolveFontPath()
		{
			string f = (this._settingForm != null && this._settingForm.cfg != null) ? this._settingForm.cfg.FontFile : null;
			if (!string.IsNullOrEmpty(f))
			{
				if (Path.IsPathRooted(f) && File.Exists(f)) return f;
				string pp = Path.Combine(Application.StartupPath, f);
				if (File.Exists(pp)) return pp;
				if (File.Exists(f)) return f;
			}
			return Path.Combine(Application.StartupPath, "16fs_new.fdat");
		}

		// Q4a: синтез глифа Ё в пустой слот 177 (копия Е из слота 197 + две точки). Только если слот 177 пуст.
		private void Patch_TrySynthesizeYo()
		{
			const int SLOT_YO = 177, SLOT_E = 197, HT = 8192;
			byte[] f = this.FONT_DATA_Main;
			if (f == null || f.Length != FontDataSize) return;
			for (int i = SLOT_YO * 32; i < SLOT_YO * 32 + 32; i++) if (f[i] != 0) return;   // уже заполнен - не трогаем
			bool eOk = false;
			for (int i = SLOT_E * 32; i < SLOT_E * 32 + 32; i++) if (f[i] != 0) { eOk = true; break; }
			if (!eOk) return;                                                               // не из чего копировать
			Array.Copy(f, SLOT_E * 32, f, SLOT_YO * 32, 32);
			f[HT + SLOT_YO] = 16;
			SetGlyphPixel(f, SLOT_YO, 0, 4);
			SetGlyphPixel(f, SLOT_YO, 0, 11);
				try { TabloPatch.SerialPortLog.Write("ШРИФТ: глиф Ё синтезирован в слот 177 (копия Е + две точки)"); } catch { }
		}

		private static void SetGlyphPixel(byte[] f, int slot, int row, int col)
		{
			int byteIndex = slot * 32 + row * 2 + (col < 8 ? 0 : 1);
			int bit = col < 8 ? (7 - col) : (15 - col);
			f[byteIndex] = (byte)(f[byteIndex] | (1 << bit));
		}

		// БЛОК 1c (итог Q4a): синтез глифа Ё на реальном полотне НЕ сработал:
		// большая Ё показывалась как "Е + пусто" (точки не отрисовались), а маленькая ё -
		// как служебный глиф "SYS": DLL-отрисовка отправляет ё в слот 1 шрифта, где лежит
		// системная картинка. Поэтому включён fallback, согласованный с заказчиком: Ё->Е,
		// ё->е текстом, прямо перед выводом на табло. Настройки, веб-панель и команды пульта
		// не затрагиваются - буква сохраняется как введена, заменяется только отрисовка.
		private static bool _yoNoteLogged = false;
		private static string Patch_BoardText(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return s;
			}
			if (s.IndexOf('Ё') < 0 && s.IndexOf('ё') < 0)
			{
				return s;
			}
			if (!_yoNoteLogged)
			{
				_yoNoteLogged = true;
				try { TabloPatch.SerialPortLog.Write("ШРИФТ: в тексте для табло заменены Ё->Е, ё->е (глифов Ё/ё в 16fs_new.fdat нет)"); } catch { }
			}
			return s.Replace('Ё', 'Е').Replace('ё', 'е');
		}

		// БЛОК 1c: звуковые файлы по умолчанию заданы коротким именем ("sirena.wav"),
		// чтобы папку программы можно было переносить целиком (Q18). Здесь короткое имя
		// разворачивается в полный путь к папке программы; если файла нет - вернётся как есть,
		// PrepareSound просто вернёт ошибку и звук останется тихим (как раньше при пустом поле).
		private static string Patch_AudioPath(string f)
		{
			try
			{
				if (string.IsNullOrEmpty(f))
				{
					return f;
				}
				if (Path.IsPathRooted(f))
				{
					return f;
				}
				return Path.Combine(Application.StartupPath, f);
			}
			catch
			{
				return f;
			}
		}

		// Q12: MM:SS, а при >= 1 часа - HH:MM:SS. tenths=true добавляет :dd (сотые).
		private static string FmtClock(TimeSpan t, bool tenths)
		{
			if (t < TimeSpan.Zero) t = TimeSpan.Zero;
			int total = (int)Math.Floor(t.TotalSeconds);
			int h = total / 3600, m = (total % 3600) / 60, s = total % 60;
			string r = (h > 0) ? h.ToString("D2") + ":" + m.ToString("D2") + ":" + s.ToString("D2") : m.ToString("D2") + ":" + s.ToString("D2");
			if (tenths) r += ":" + (t.Milliseconds / 10).ToString("D2");
			return r;
		}

		// ================== БЛОК 4: НОВЫЕ ПОЛЯ И ПОМОЩНИКИ ==================

		// F9-05: кэш ссылок на динамические контролы (см. Patch_CacheDynamicControls).
		private ComboBox _patchCbSpeed;
		private Control _patchLSpeed;
		private ListBox _patchLbArchive;

		private ComboBox Patch_GetCbSpeed()
		{
			if (this._patchCbSpeed != null && !this._patchCbSpeed.IsDisposed)
			{
				return this._patchCbSpeed;
			}
			Control[] array = new Control[0];
			if (this.but_TimeReset != null && this.but_TimeReset.Parent != null)
			{
				array = this.but_TimeReset.Parent.Controls.Find("cbSpeed", true);
			}
			if (array.Length == 0)
			{
				array = base.Controls.Find("cbSpeed", true);
			}
			this._patchCbSpeed = (array.Length != 0) ? (array[0] as ComboBox) : null;
			return this._patchCbSpeed;
		}

		private Control Patch_GetLSpeed()
		{
			if (this._patchLSpeed != null && !this._patchLSpeed.IsDisposed)
			{
				return this._patchLSpeed;
			}
			Control[] array = base.Controls.Find("lSpeed", true);
			this._patchLSpeed = (array.Length != 0) ? array[0] : null;
			return this._patchLSpeed;
		}

		private ListBox Patch_GetArchiveList()
		{
			if (this._patchLbArchive != null && !this._patchLbArchive.IsDisposed)
			{
				return this._patchLbArchive;
			}
			Control[] array = base.Controls.Find("lbTimeArchive", true);
			this._patchLbArchive = (array.Length != 0) ? (array[0] as ListBox) : null;
			return this._patchLbArchive;
		}

		// F9-05/F5-06: кэш наполняется в конце frmMain_Load (когда динамические контролы
		// уже созданы); заодно задаются явные границы числовых полей.
		private void Patch_CacheDynamicControls()
		{
			try
			{
				this.Patch_GetCbSpeed();
				this.Patch_GetLSpeed();
				this.Patch_GetArchiveList();
				// F5-06: счёт 0..99, период 1..9 — мусорные значения с пульта/веба
				// упираются в границы вместо исключения.
				this.nUD_Score_1.Minimum = 0m;
				this.nUD_Score_1.Maximum = 99m;
				this.nUD_Score_2.Minimum = 0m;
				this.nUD_Score_2.Maximum = 99m;
				this.nUD_Period.Minimum = 1m;
			}
			catch (Exception ex)
			{
				TabloPatch.Diag.Note("Patch_CacheDynamicControls", ex);
			}
		}

		// F5-06: безопасное присвоение NumericUpDown с обрезкой до Minimum/Maximum.
		private void Patch_SetNumeric(NumericUpDown nud, decimal v)
		{
			if (nud == null)
			{
				return;
			}
			if (v < nud.Minimum)
			{
				v = nud.Minimum;
			}
			if (v > nud.Maximum)
			{
				v = nud.Maximum;
			}
			nud.Value = v;
		}

		// F2-03: кэш tablo_stream.txt — перечитывается не чаще раза в 2 с
		// (раньше файл читался при КАЖДОМ изменении картинки, до 100 раз/с).
		private int _patchStreamCfgTick;
		private bool _patchStreamCfgNote;
		private string _patchStreamVmixApi = "0";
		private string _patchStreamVmixHost = "";
		private string _patchStreamVmixInput = "Title.xaml";
		private string _patchStreamVmixField = "Clock.Text";
		private string _patchStreamUdp = "0";
		private string _patchStreamUdpHost = "";
		private string _patchStreamFile = "0";

		private void Patch_LoadStreamCfg()
		{
			int now = Environment.TickCount;
			if (this._patchStreamCfgTick != 0 && now - this._patchStreamCfgTick < 2000)
			{
				return;
			}
			this._patchStreamCfgTick = now;
			string vmixApi = "0";
			string vmixHost = "";
			string vmixInput = "Title.xaml";
			string vmixField = "Clock.Text";
			string udp = "0";
			string udpHost = "";
			string file = "0";
			try
			{
				string path = Path.Combine(Application.StartupPath, "tablo_stream.txt");
				if (File.Exists(path))
				{
					foreach (string ln in TabloPatch.TextIO.ReadAllLines(path))
					{
						int eq = ln.IndexOf('=');
						if (eq < 0)
						{
							continue;
						}
						string k = ln.Substring(0, eq).Trim();
						string v = ln.Substring(eq + 1).Trim();
						if (k == "VmixApi") { vmixApi = v; }
						else if (k == "VmixHost") { vmixHost = v; }
						else if (k == "VmixInput") { vmixInput = v; }
						else if (k == "VmixField") { vmixField = v; }
						else if (k == "Udp") { udp = v; }
						else if (k == "UdpHost") { udpHost = v; }
						else if (k == "File") { file = v; }
					}
				}
			}
			catch (Exception ex)
			{
				// F8-07: сбой чтения виден в логе (один раз), остаются последние известные значения
				if (!this._patchStreamCfgNote)
				{
					this._patchStreamCfgNote = true;
					TabloPatch.SerialPortLog.Write("ПОТОК: tablo_stream.txt не читается (" + ex.Message + ") — использую последние известные значения");
				}
				return;
			}
			this._patchStreamVmixApi = vmixApi;
			this._patchStreamVmixHost = vmixHost;
			this._patchStreamVmixInput = vmixInput;
			this._patchStreamVmixField = vmixField;
			this._patchStreamUdp = udp;
			this._patchStreamUdpHost = udpHost;
			this._patchStreamFile = file;
		}

		// F8-09: «быстрая» атомарная запись маленьких файлов (tablo_time.txt,
		// tablo_full.json): tmp -> File.Replace. Без WriteThrough — файлы меняются
		// каждую секунду, ресурс диска важнее; обрыв питания оставит ЦЕЛЫЙ старый файл.
		private static void Patch_WriteFileQuickAtomic(string path, string content)
		{
			string tmp = path + ".tmp";
			File.WriteAllText(tmp, content, new UTF8Encoding(false));
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

		// F9-10: кэш значения овертайма (2 с) + TryParse вместо Parse + честный лог (F8-07).
		private int _patchOtCacheSec = -1;
		private int _patchOtCacheTick;
		private int _patchOtFileNote;   // 0=нет, 1=«нет файла», 2=«битый файл»

		private int Patch_OvertimeSecCached()
		{
			int now = Environment.TickCount;
			if (this._patchOtCacheSec > 0 && now - this._patchOtCacheTick < 2000)
			{
				return this._patchOtCacheSec;
			}
			int sec = 300;   // заводские 5:00
			int note = 0;
			try
			{
				string path = Path.Combine(Application.StartupPath, "tablo_overtime.txt");
				if (!File.Exists(path))
				{
					note = 1;
				}
				else
				{
					TimeSpan ts;
					string raw = TabloPatch.TextIO.ReadAllText(path).Trim();
					if (TimeSpan.TryParse(raw, out ts) && ts.TotalSeconds > 0.0 && ts.TotalHours <= 6.0)
					{
						sec = (int)ts.TotalSeconds;
					}
					else
					{
						note = 2;
					}
				}
			}
			catch
			{
				note = 2;
			}
			if (note != 0 && this._patchOtFileNote != note)
			{
				this._patchOtFileNote = note;
				if (note == 1)
				{
					TabloPatch.SerialPortLog.Write("ОВЕРТАЙМ: tablo_overtime.txt отсутствует — используется значение по умолчанию 5:00 (файл создастся при первой ручной установке овертайма)");
				}
				else
				{
					TabloPatch.SerialPortLog.Write("ОВЕРТАЙМ: tablo_overtime.txt повреждён или значение вне диапазона 1 с..6 ч — используется 5:00");
				}
			}
			else if (note == 0)
			{
				this._patchOtFileNote = 0;
			}
			this._patchOtCacheSec = sec;
			this._patchOtCacheTick = Environment.TickCount;
			return sec;
		}

		// F4-14: обрезка длинных строк для веб-витрины (клетка табло — 14 знакомест).
		private static string Patch_Fit(string s, int max)
		{
			if (s == null)
			{
				return "";
			}
			if (s.Length <= max)
			{
				return s;
			}
			return s.Substring(0, max);
		}

		// F2-02b: ShowPenalty x4 вызывается только при реальном изменении списков
		// (в ветке «матч окончен» раньше это происходило каждые 10 мс).
		private string _patchLastPenKey;

		private void Patch_ShowPenaltiesOnChanged()
		{
			string key = this.Patch_JoinPenalties(this.lB_DelPeople_A) + "|" + this.Patch_JoinPenalties(this.lB_DelPeople_B);
			if (key == this._patchLastPenKey)
			{
				return;
			}
			this._patchLastPenKey = key;
			this.ShowPenalty(0, this.lB_DelPeople_A, this.gs_LWindow_2);
			this.ShowPenalty(1, this.lB_DelPeople_A, this.gs_LWindow_3);
			this.ShowPenalty(0, this.lB_DelPeople_B, this.gs_RWindow_2);
			this.ShowPenalty(1, this.lB_DelPeople_B, this.gs_RWindow_3);
		}

		// F2-02b: геометрия центрального окна переключается ОДИН раз в момент смены
		// формата (раньше thf.Stop()/Start() дёргались каждый тик).
		private int _patchCentreWideApplied = -1;

		// F6-09: временная подсказка в статус-строку.
		private void Patch_StatusHint(string msg)
		{
			try
			{
				if (this.statusStrip1.Items.Count > 2)
				{
					this.statusStrip1.Items[2].Text = msg;
				}
			}
			catch
			{
			}
		}

		// F6-12: человекочитаемое имя текущего режима для статус-строки.
		private string Patch_ModeName()
		{
			if (this._TabloMode == frmMain.tabloMode.prompt)
			{
				return "Заставка";
			}
			if (this._TabloMode == frmMain.tabloMode.video)
			{
				return "Видео";
			}
			if (this._GameMode == frmMain.gameMode.GameStarted)
			{
				return "идёт период";
			}
			if (this._GameMode == frmMain.gameMode.Timeout)
			{
				return "тайм-аут";
			}
			if (this._GameMode == frmMain.gameMode.Break)
			{
				return "перерыв";
			}
			return "остановлено";
		}

		// F5-09: индикатор «пульт живой» — время последней принятой команды.
		private DateTime _patchLastPultCmdUtc = DateTime.MinValue;

		// F4-06 (Q13): предупреждение о символах, которых нет в шрифте табло.
		// Текст НЕ заменяется (решение заказчика) — только честное предупреждение,
		// один раз за комбинацию «поле + набор символов».
		private string _patchBadCharsNoted = "";

		private static bool Patch_IsBoardChar(char c)
		{
			if (c >= 'А' && c <= 'Я') { return true; }
			if (c >= 'а' && c <= 'я') { return true; }
			if (c == 'Ё' || c == 'ё') { return true; }   // отрисовывается как Е/е (Patch_BoardText)
			if (c >= 'A' && c <= 'Z') { return true; }
			if (c >= 'a' && c <= 'z') { return true; }
			if (c >= '0' && c <= '9') { return true; }
			return " -.:/(),_+".IndexOf(c) >= 0;
		}

		private void Patch_WarnBoardChars(string s, string field, bool popup)
		{
			try
			{
				if (string.IsNullOrEmpty(s))
				{
					return;
				}
				System.Text.StringBuilder bad = new System.Text.StringBuilder();
				foreach (char c in s)
				{
					if (!Patch_IsBoardChar(c) && bad.ToString().IndexOf(c) < 0)
					{
						bad.Append(c);
					}
				}
				if (bad.Length == 0)
				{
					return;
				}
				string key = field + ":" + bad.ToString();
				if (this._patchBadCharsNoted.IndexOf(key) >= 0)
				{
					return;
				}
				this._patchBadCharsNoted += key + ";";
				TabloPatch.SerialPortLog.Write("СИМВОЛЫ: поле «" + field + "» содержит символы, которых нет в шрифте табло: «" + bad + "» — на полотне отобразятся мусором/квадратами. Замените их (автоматически текст НЕ правится, Q13).");
				if (popup)
				{
					MessageBox.Show("В поле «" + field + "» есть символы, которых нет в шрифте табло:\r\n«" + bad + "»\r\n\r\nНа полотне они отобразятся мусором или квадратами.\r\nЗамените их на обычные буквы/цифры (программа текст не меняет).", "Табло — неподдерживаемые символы", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				}
			}
			catch
			{
			}
		}

		// F5-08: одноразовая диагностика «кракозябр» из пульта (KOI8-R вместо CP1251):
		// пишем коды подозрительных символов, чтобы по HEX-логу понять причину.
		private bool _patchPultCharsetNoted;

		private void Patch_CheckPultText(string s, string src)
		{
			try
			{
				if (this._patchPultCharsetNoted || string.IsNullOrEmpty(s))
				{
					return;
				}
				System.Text.StringBuilder susp = new System.Text.StringBuilder();
				foreach (char c in s)
				{
					if (!Patch_IsBoardChar(c))
					{
						susp.Append("[U+").Append(((int)c).ToString("X4")).Append("]");
						if (susp.Length > 40)
						{
							break;
						}
					}
				}
				if (susp.Length > 0)
				{
					this._patchPultCharsetNoted = true;
					TabloPatch.SerialPortLog.Write("ПУЛЬТ " + src + ": в принятом тексте есть неподдерживаемые символы " + susp + " — возможно, пульт передаёт имя в другой кодировке (например, KOI8-R вместо Windows-1251). Принятый текст: «" + s + "»");
				}
			}
			catch
			{
			}
		}

		// F9-09: FontData (~8.4 КБ) передаётся контролу только если ссылка изменилась —
		// повторные присваивания больше не инициируют лишнюю перерисовку.
		private readonly System.Collections.Generic.Dictionary<GraphString, byte[]> _patchFontAssigned = new System.Collections.Generic.Dictionary<GraphString, byte[]>();

		private void Patch_SetFontDataOnce(GraphString gs)
		{
			// БЛОК 4, F9-09: у GraphString.FontData нет геттера — запоминаем последнюю
			// назначенную ссылку в словаре и НЕ переназначаем тот же массив: повторные
			// присваивания заставляли контрол перерисовывать все глифы впустую.
			if (gs == null)
			{
				return;
			}
			byte[] prev;
			if (this._patchFontAssigned.TryGetValue(gs, out prev) && object.ReferenceEquals(prev, this.FONT_DATA_Main))
			{
				return;
			}
			gs.FontData = this.FONT_DATA_Main;
			this._patchFontAssigned[gs] = this.FONT_DATA_Main;
		}

		// F8-08: периодическая проба записи в папку программы (~раз в 30 с).
		// Оператор узнаёт о потере прав СРАЗУ, а не когда потеряются настройки.
		private bool _patchProbeWarned;
		private bool _patchUacPrompted;

		private void Patch_ProbeWriteAccess()
		{
			if (this._patchProbeWarned)
			{
				return;
			}
			try
			{
				string probe = Path.Combine(Application.StartupPath, "write_probe.tmp");
				File.WriteAllText(probe, "1");
				File.Delete(probe);
			}
			catch (Exception ex)
			{
				this._patchProbeWarned = true;
				TabloPatch.CrashLog.Append("ПРАВА", "папка программы не доступна на запись: " + ex.Message);
				TabloPatch.SerialPortLog.Write("ПРАВА: НЕТ ЗАПИСИ в папку программы (" + Application.StartupPath + "): " + ex.Message + " — настройки, логи и автосохранение матча НЕ сохраняются!");
				this.Patch_OfferElevation(ex.Message);
			}
		}

		// F8-08 (Q19): предложение перезапуска с правами администратора — ОДИН раз за сеанс.
		private void Patch_OfferElevation(string reason)
		{
			if (this._patchUacPrompted)
			{
				return;
			}
			this._patchUacPrompted = true;
			try
			{
				if (Environment.OSVersion.Version.Major >= 6)
				{
					DialogResult r = MessageBox.Show("Нет прав на запись в папку программы:\r\n" + Application.StartupPath + "\r\n(" + reason + ")\r\n\r\nНастройки, логи и автосохранение матча НЕ сохраняются.\r\n\r\nПерезапустить Табло с правами администратора? (появится запрос UAC)\r\nТекущее окно закроется; состояние матча будет предложено восстановить.", "Табло — нет прав на запись", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
					if (r == DialogResult.Yes)
					{
						try
						{
							this.Patch_AutosaveState(true);
						}
						catch
						{
						}
						System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath);
						psi.Verb = "runas";
						psi.WorkingDirectory = Application.StartupPath;
						System.Diagnostics.Process.Start(psi);
						this.Close();
					}
				}
				else
				{
					MessageBox.Show("Нет прав на запись в папку программы:\r\n" + Application.StartupPath + "\r\n\r\nНа Windows XP войдите в систему учётной записью администратора или перенесите папку программы туда, где у вас есть права на запись (например, C:\\TestTablo).", "Табло — нет прав на запись", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				}
			}
			catch (Exception ex)
			{
				TabloPatch.CrashLog.Append("ПРАВА", "не удалось предложить перезапуск от администратора: " + ex.Message);
			}
		}

		// F6-08: защищённый разбор строки архива времени
		// («№N | MM:SS | A: 05 02:00; ... | B: ...»). Без исключений.
		// F6-08 + ПУНКТ 12: защищённый разбор строки архива времени.
		// Формат: «№N | MM:SS | A: 05 02:00; ... | B: ... | П: 2 | С: 3:2».
		// Два последних поля появились в пункте 12; в старых записях их нет —
		// тогда period/score возвращаются нулями (вызывающий показывает «—»).
		// Без исключений: любая «битая» строка даёт false.
		private bool Patch_TryParseSnapshot(string text, out TimeSpan remain, out string[] pensA, out string[] pensB, out int period, out int scoreH, out int scoreA)
		{
			remain = TimeSpan.Zero;
			pensA = new string[0];
			pensB = new string[0];
			period = 0;
			scoreH = 0;
			scoreA = 0;
			try
			{
				if (string.IsNullOrEmpty(text))
				{
					return false;
				}
				string[] parts = text.Split(new char[] { '|' });
				if (parts.Length < 2)
				{
					return false;
				}
				string[] ms = parts[1].Trim().Split(new char[] { ':' });
				int mm;
				int ss;
				if (ms.Length != 2 || !int.TryParse(ms[0].Trim(), out mm) || !int.TryParse(ms[1].Trim(), out ss))
				{
					return false;
				}
				if (mm < 0 || ss < 0 || ss > 59)
				{
					return false;
				}
				remain = TimeSpan.FromSeconds((double)(mm * 60 + ss));
				if (parts.Length > 2)
				{
					string t2 = parts[2].Trim();
					if (t2.StartsWith("A:"))
					{
						t2 = t2.Substring(2).Trim();
					}
					if (t2.Length > 0)
					{
						pensA = t2.Split(new string[] { "; " }, StringSplitOptions.RemoveEmptyEntries);
					}
				}
				if (parts.Length > 3)
				{
					string t3 = parts[3].Trim();
					if (t3.StartsWith("B:"))
					{
						t3 = t3.Substring(2).Trim();
					}
					if (t3.Length > 0)
					{
						pensB = t3.Split(new string[] { "; " }, StringSplitOptions.RemoveEmptyEntries);
					}
				}
				// ПУНКТ 12: период и счёт — поля необязательные (совместимость со старым архивом)
				if (parts.Length > 4)
				{
					string t4 = parts[4].Trim();
					if (t4.StartsWith("П:"))
					{
						int pv;
						if (int.TryParse(t4.Substring(2).Trim(), out pv) && pv > 0)
						{
							period = pv;
						}
					}
				}
				if (parts.Length > 5)
				{
					string t5 = parts[5].Trim();
					if (t5.StartsWith("С:"))
					{
						string[] sc = t5.Substring(2).Trim().Split(new char[] { ':' });
						int sh;
						int sa;
						if (sc.Length == 2 && int.TryParse(sc[0].Trim(), out sh) && int.TryParse(sc[1].Trim(), out sa))
						{
							scoreH = sh;
							scoreA = sa;
						}
					}
				}
				return true;
			}
			catch
			{
				return false;
			}
		}

		// ================== ПУНКТ 12: ПРИМЕНЕНИЕ СНИМКА АРХИВА ВРЕМЕНИ ==================
		// Общая реализация для десктопной кнопки «Применить время» и для веб-панели.
		// Что делает (как и раньше): останавливает часы, ставит остаток времени периода,
		// ЗАМЕНЯЕТ текущие удаления удалениями из снимка. Счёт и номер периода не меняет.
		// source — только для журнала (видно, откуда пришла команда).
		private void Patch_ApplySnapshotCore(string text, string source)
		{
			try
			{
				TimeSpan snapRemain;
				string[] snapPensA;
				string[] snapPensB;
				int snapPeriod;
				int snapScoreH;
				int snapScoreA;
				if (!this.Patch_TryParseSnapshot(text, out snapRemain, out snapPensA, out snapPensB, out snapPeriod, out snapScoreH, out snapScoreA))
				{
					TabloPatch.SerialPortLog.Write("АРХИВ (" + source + "): запись не разбирается, применение отменено: " + text);
					return;
				}
				string head = text.Split(new char[] { '|' })[0].Trim();
				this.Cmd_Stop();
				TimeSpan timeSpan = snapRemain;
				if (timeSpan > this._gameTotalTime)
				{
					timeSpan = this._gameTotalTime;
				}
				this._gameTime = this._gameTotalTime - timeSpan;
				this.lB_DelPeople_A.Items.Clear();
				this.lB_DelPeople_B.Items.Clear();
				for (int ia = 0; ia < snapPensA.Length; ia++)
				{
					this.lB_DelPeople_A.Items.Add(snapPensA[ia]);
				}
				for (int ib = 0; ib < snapPensB.Length; ib++)
				{
					this.lB_DelPeople_B.Items.Add(snapPensB[ib]);
				}
				ComboBox cbSnap = this.Patch_GetCbSpeed();   // БЛОК 4, F9-05
				if (cbSnap != null)
				{
					cbSnap.Tag = (int)Math.Ceiling(this._gameTime.TotalSeconds);
				}
				this._gameOver = false;
				this.ShowPenalty(0, this.lB_DelPeople_A, this.gs_LWindow_2);
				this.ShowPenalty(1, this.lB_DelPeople_A, this.gs_LWindow_3);
				this.ShowPenalty(0, this.lB_DelPeople_B, this.gs_RWindow_2);
				this.ShowPenalty(1, this.lB_DelPeople_B, this.gs_RWindow_3);
				this.TabloMode();
				this.statusShow();
				TabloPatch.SerialPortLog.Write("АРХИВ (" + source + "): применён снимок «" + head + "» — остаток "
					+ FmtClock(snapRemain, false) + ", удаления А=" + snapPensA.Length.ToString() + " Б=" + snapPensB.Length.ToString()
					+ (snapPeriod > 0 ? (", в снимке период " + snapPeriod.ToString() + " счёт " + snapScoreH.ToString() + ":" + snapScoreA.ToString() + " (не изменялись)") : ""));
			}
			catch (Exception exSnap)
			{
				TabloPatch.Diag.Note("Применение снимка архива времени", exSnap);
			}
		}

		// ПУНКТ 12: применение снимка из веб-панели (команда arch_apply, p = индекс записи).
		private void Patch_WebArchiveApply(int index)
		{
			ListBox lb = this.Patch_GetArchiveList();
			if (lb == null)
			{
				TabloPatch.SerialPortLog.Write("АРХИВ (веб): список архива недоступен — применение отменено");
				return;
			}
			if (index < 0 || index >= lb.Items.Count)
			{
				TabloPatch.SerialPortLog.Write("АРХИВ (веб): индекс " + index.ToString() + " вне диапазона 0.." + (lb.Items.Count - 1).ToString() + " — применение отменено");
				return;
			}
			string text = lb.Items[index] as string;
			if (text == null)
			{
				return;
			}
			this.Patch_ApplySnapshotCore(text, "веб-панель, запись №" + (index + 1).ToString());
		}

		// ПУНКТ 12: последние max записей архива в виде JSON-массива для state.json.
		// Отдаются НОВЫЕ записи первыми — в веб-панели последние стопы видны сверху.
		// Для каждой: время матча, активные позиции, счёт, период + индекс для применения.
		private string Patch_ArchiveJson(int max)
		{
			System.Text.StringBuilder sbA = new System.Text.StringBuilder();
			sbA.Append("[");
			try
			{
				ListBox lb = this.Patch_GetArchiveList();
				if (lb != null && lb.Items.Count > 0 && max > 0)
				{
					int first = lb.Items.Count - max;
					if (first < 0)
					{
						first = 0;
					}
					bool firstItem = true;
					for (int i = lb.Items.Count - 1; i >= first; i--)
					{
						string sItem = lb.Items[i] as string;
						if (sItem == null)
						{
							continue;
						}
						TimeSpan rem;
						string[] pa;
						string[] pb;
						int per;
						int sh;
						int sa;
						if (!this.Patch_TryParseSnapshot(sItem, out rem, out pa, out pb, out per, out sh, out sa))
						{
							continue;
						}
						if (!firstItem)
						{
							sbA.Append(",");
						}
						firstItem = false;
						int remSec = (int)Math.Ceiling(rem.TotalSeconds);
						if (remSec < 0)
						{
							remSec = 0;
						}
						string head = sItem.Split(new char[] { '|' })[0].Trim();
						sbA.Append("{\"n\":\"").Append(this.JsonEsc(head)).Append("\",");
						sbA.Append("\"t\":").Append(remSec.ToString()).Append(",");
						sbA.Append("\"period\":").Append(per.ToString()).Append(",");
						sbA.Append("\"score_h\":").Append(sh.ToString()).Append(",");
						sbA.Append("\"score_a\":").Append(sa.ToString()).Append(",");
						sbA.Append("\"pen_h\":\"").Append(this.JsonEsc(string.Join("; ", pa))).Append("\",");
						sbA.Append("\"pen_a\":\"").Append(this.JsonEsc(string.Join("; ", pb))).Append("\",");
						sbA.Append("\"idx\":").Append(i.ToString()).Append("}");
					}
				}
			}
			catch (Exception exArch)
			{
				TabloPatch.Diag.Note("Архив времени в state.json", exArch);
			}
			sbA.Append("]");
			return sbA.ToString();
		}

		// F6-16: база отсчёта предматчевого таймера — ОТДЕЛЬНО от _storedTime
		// (раньше одно и то же поле использовалось и для миллисекунд матча, и для
		// тиков предстарта: старт матча во время заставки сбивал обратный отсчёт).
		private TimeSpan _patchBeforeRef;

		// F1-01: диагностика Managed DirectX (нужен TabloHockey.dll) - понятное сообщение вместо TypeLoadException
		private void Patch_CheckRuntime()
		{
			try
			{
				System.Reflection.Assembly.Load("Microsoft.DirectX.Direct3D, Version=1.0.2902.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
			}
			catch (Exception ex)
			{
				string msg = "Не найден компонент Microsoft.DirectX.Direct3D (1.0.2902.0).\r\n\r\nБез него табло не отрисуется. Скопируйте Microsoft.DirectX.dll и Microsoft.DirectX.Direct3D.dll\r\nв папку программы (из комплекта) ИЛИ установите DirectX End-User Runtime (June 2010).\r\n\r\nТехнически: " + ex.Message;
				MessageBox.Show(msg, "Табло — не хватает компонента", MessageBoxButtons.OK, MessageBoxIcon.Stop);
				TabloPatch.CrashLog.Append("RUNTIME", msg);   // БЛОК 4, F8-06
			}
		}

		// Token: 0x06000082 RID: 130 RVA: 0x0000B130 File Offset: 0x00009330
		private void PrepareVideoFile(ListBox playList)
		{
			string videoFilePath = this.VideoFilePath;
			this.VideoFilePath = this.GetNextVideoPath(playList, this.VideoFilePath);
			int num = playList.Items.Count;
			while (!File.Exists(this.VideoFilePath))
			{
				if (num == 0)
				{
					this.VideoFilePath = string.Empty;
					break;
				}
				this.VideoFilePath = this.GetNextVideoPath(playList, this.VideoFilePath);
				num--;
			}
			if (this.VideoFilePath != string.Empty && this.vpf != null)
			{
				if (this.VideoFilePath != videoFilePath)
				{
					this.CurVideoPosition = 0.0;
				}
				try
				{
					this.vpf.PreLoad(this.VideoFilePath);
				}
				catch
				{
				}
			}
		}

		// Token: 0x06000083 RID: 131 RVA: 0x0000B1F8 File Offset: 0x000093F8
		private string GetNextVideoPath(ListBox playList, string vPath)
		{
			if (playList.Items.Count == 0)
			{
				return string.Empty;
			}
			string result;
			if (vPath == string.Empty)
			{
				result = (string)playList.Items[0];
			}
			else
			{
				int num = playList.Items.IndexOf(vPath);
				if (this.VideoPlayed)
				{
					num++;
				}
				if (num >= playList.Items.Count || num < 0)
				{
					num = 0;
				}
				result = (string)playList.Items[num];
			}
			return result;
		}

		// Token: 0x06000084 RID: 132 RVA: 0x0000B27C File Offset: 0x0000947C
		private void OpenVideo(ListBox playList)
		{
			if (this.vpf == null)
			{
				if (this.thf != null)
				{
					this.CloseTabloHockey();
				}
				if (this.thfPrompt != null)
				{
					this.CloseTabloHockeyPrompt();
				}
				this.vpf = new VideoPlayerForm();
				this.vpf.FormBorderStyle = FormBorderStyle.None;
				this.vpf.TopMost = this._settingForm.cfg.TopWindow;
				this.vpf.ControlBox = false;
				this.vpf.ShowInTaskbar = false;
				this.vpf.StartPosition = FormStartPosition.Manual;
				this.vpf.Left = this._settingForm.cfg.Left;
				this.vpf.Top = this._settingForm.cfg.Top;
				this.vpf.Width = 240;
				this.vpf.Height = 160;
				this.vpf.OnVideoPlayerStop += this.vpf_OnVideoPlayerStop;
				this.vpf.OnVideoPlayerLoadFile += this.vpf_OnVideoPlayerLoadFile;
				this.VideoPlayed = false;
				this.CreateBackgroundWindow(this.vpf.Width, this.vpf.Height);
				this._NowPlayList = playList;
				try
				{
					this.PrepareVideoFile(playList);
				}
				catch (Exception ex)
				{
					MessageBox.Show("Ошибка запуска видео: " + ex.Message, "Табло", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				}
				try
				{
					this.vpf.Show();
					this.vpf.BringToFront();
				}
				catch
				{
				}
			}
		}

		// Token: 0x06000085 RID: 133 RVA: 0x00002478 File Offset: 0x00000678
		private void CloseVideoPlayer()
		{
			if (this.vpf != null)
			{
				this.CurVideoPosition = this.vpf.GetVideoPosition();
				this.vpf.Close();
				this.vpf = null;
				this._NowPlayList = null;
			}
		}

		// Token: 0x06000086 RID: 134 RVA: 0x0000B418 File Offset: 0x00009618
		private void tB_MainText_TextChanged(object sender, EventArgs e)
		{
			MaskedTextBox maskedTextBox = (MaskedTextBox)sender;
			if (this.gs_Main != null)
			{
				this.gs_Main.Text = Patch_BoardText(maskedTextBox.Text);   // БЛОК 1c (Q4a): Ё->Е только для табло
			}
		}

		// Token: 0x06000087 RID: 135 RVA: 0x0000B448 File Offset: 0x00009648
		private void tB_LWindow_1_TextChanged(object sender, EventArgs e)
		{
			MaskedTextBox maskedTextBox = (MaskedTextBox)sender;
			if (this.gs_LWindow_1 != null)
			{
				this.gs_LWindow_1.Text = maskedTextBox.Text;
			}
		}

		// Token: 0x06000088 RID: 136 RVA: 0x0000B478 File Offset: 0x00009678
		private void tB_CentreWindow_TextChanged(object sender, EventArgs e)
		{
			MaskedTextBox maskedTextBox = (MaskedTextBox)sender;
			if (this.gs_CentreWindow != null)
			{
				this.gs_CentreWindow.Text = maskedTextBox.Text;
			}
		}

		// Token: 0x06000089 RID: 137 RVA: 0x0000B4A8 File Offset: 0x000096A8
		private void tB_RWindow_1_TextChanged(object sender, EventArgs e)
		{
			MaskedTextBox maskedTextBox = (MaskedTextBox)sender;
			if (this.gs_RWindow_1 != null)
			{
				this.gs_RWindow_1.Text = maskedTextBox.Text;
			}
		}

		// Token: 0x0600008A RID: 138 RVA: 0x0000B4D8 File Offset: 0x000096D8
		private void tB_CentreWindow_small_TextChanged(object sender, EventArgs e)
		{
			MaskedTextBox maskedTextBox = (MaskedTextBox)sender;
			if (this.gs_CentreWindow_small != null)
			{
				this.gs_CentreWindow_small.Text = maskedTextBox.Text;
			}
		}

		// Token: 0x0600008B RID: 139 RVA: 0x0000B508 File Offset: 0x00009708
		private void tB_LWindow_2_TextChanged(object sender, EventArgs e)
		{
			MaskedTextBox maskedTextBox = (MaskedTextBox)sender;
			if (this.gs_LWindow_2 != null)
			{
				this.gs_LWindow_2.Text = maskedTextBox.Text;
			}
		}

		// Token: 0x0600008C RID: 140 RVA: 0x0000B538 File Offset: 0x00009738
		private void tB_LWindow_3_TextChanged(object sender, EventArgs e)
		{
			MaskedTextBox maskedTextBox = (MaskedTextBox)sender;
			if (this.gs_LWindow_3 != null)
			{
				this.gs_LWindow_3.Text = maskedTextBox.Text;
			}
		}

		// Token: 0x0600008D RID: 141 RVA: 0x0000B568 File Offset: 0x00009768
		private void tB_RWindow_2_TextChanged(object sender, EventArgs e)
		{
			MaskedTextBox maskedTextBox = (MaskedTextBox)sender;
			if (this.gs_RWindow_2 != null)
			{
				this.gs_RWindow_2.Text = maskedTextBox.Text;
			}
		}

		// Token: 0x0600008E RID: 142 RVA: 0x0000B598 File Offset: 0x00009798
		private void tB_RWindow_3_TextChanged(object sender, EventArgs e)
		{
			MaskedTextBox maskedTextBox = (MaskedTextBox)sender;
			if (this.gs_RWindow_3 != null)
			{
				this.gs_RWindow_3.Text = maskedTextBox.Text;
			}
		}

		// Token: 0x0600008F RID: 143 RVA: 0x000024AC File Offset: 0x000006AC
		private void выходToolStripMenuItem_Click(object sender, EventArgs e)
		{
			base.Close();
		}

		// Token: 0x06000090 RID: 144 RVA: 0x0000B5C8 File Offset: 0x000097C8
		private void сохранитьСписокВидеоToolStripMenuItem_Click(object sender, EventArgs e)
		{
			SaveFileDialog saveFileDialog = new SaveFileDialog();
			saveFileDialog.Filter = "Media Playlist Files|*.mpf|All Files|*.*";
			saveFileDialog.DefaultExt = ".mpf";
			if (saveFileDialog.ShowDialog() == DialogResult.OK)
			{
				StringBuilder stringBuilder = new StringBuilder();
				stringBuilder.AppendLine("--VideoFiles--");
				foreach (object obj in this.cLB_VideoFiles.Items)
				{
					string value = (string)obj;
					stringBuilder.AppendLine(value);
				}
				stringBuilder.AppendLine("--TimeoutVideoFiles--");
				foreach (object obj2 in this.cLB_TimeoutVideoFiles.Items)
				{
					string value2 = (string)obj2;
					stringBuilder.AppendLine(value2);
				}
				File.WriteAllText(saveFileDialog.FileName, stringBuilder.ToString());
				this._settingForm.cfg.MediaPlaylistPath = saveFileDialog.FileName;
			}
		}

		// Token: 0x06000091 RID: 145 RVA: 0x0000B6F0 File Offset: 0x000098F0
		private void загрузитьСписокВидеоToolStripMenuItem_Click(object sender, EventArgs e)
		{
			OpenFileDialog openFileDialog = new OpenFileDialog();
			openFileDialog.Filter = "Media Playlist Files|*.mpf|All Files|*.*";
			if (DialogResult.OK == openFileDialog.ShowDialog())
			{
				this.LoadVideoList(openFileDialog.FileName);
			}
		}

		// Token: 0x06000092 RID: 146 RVA: 0x0000B724 File Offset: 0x00009924
		private void загToolStripMenuItem_Click(object sender, EventArgs e)
		{
			OpenFileDialog openFileDialog = new OpenFileDialog();
			openFileDialog.Filter = "Script Playlist Files|*.spf|All Files|*.*";
			if (DialogResult.OK == openFileDialog.ShowDialog())
			{
				string[] items = File.ReadAllLines(openFileDialog.FileName);
				this.cLB_ScriptFiles.Items.Clear();
				this.cLB_ScriptFiles.Items.AddRange(items);
				this._settingForm.cfg.ScriptPlaylistPath = openFileDialog.FileName;
			}
		}

		// Token: 0x06000093 RID: 147 RVA: 0x0000B790 File Offset: 0x00009990
		private void сохранитьСписокШаблоновToolStripMenuItem_Click(object sender, EventArgs e)
		{
			SaveFileDialog saveFileDialog = new SaveFileDialog();
			saveFileDialog.Filter = "Script Playlist Files|*.spf|All Files|*.*";
			saveFileDialog.DefaultExt = ".spf";
			if (saveFileDialog.ShowDialog() == DialogResult.OK)
			{
				StringBuilder stringBuilder = new StringBuilder();
				foreach (object obj in this.cLB_ScriptFiles.Items)
				{
					string value = (string)obj;
					stringBuilder.AppendLine(value);
				}
				File.WriteAllText(saveFileDialog.FileName, stringBuilder.ToString());
				this._settingForm.cfg.ScriptPlaylistPath = saveFileDialog.FileName;
			}
		}

		// Token: 0x06000094 RID: 148 RVA: 0x0000B844 File Offset: 0x00009A44
		private void настройкиToolStripMenuItem_Click(object sender, EventArgs e)
		{
			try
			{
				// БЛОК 3b: если окно настроек не создалось при старте (ошибка config.xml),
				// пробуем создать его снова сейчас, а при неудаче показываем понятное
				// сообщение вместо «В экземпляре объекта не задана ссылка на объект.»
				if (this._settingForm == null)
				{
					try
					{
						this._settingForm = new SettingForm();
					}
					catch (Exception exrs)
					{
						TabloPatch.CrashLog.Append("SETTINGS", "окно настроек недоступно: " + exrs.ToString());   // БЛОК 4, F8-06
						MessageBox.Show("Настройки недоступны: " + exrs.Message + "\r\nПодробности записаны в crash_log.txt.\nЕсли ошибка повторяется — удалите config.xml в папке программы и перезапустите Табло: заводские настройки создадутся заново.", "Табло — Настройки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
						return;
					}
				}
				if (this._settingForm.ShowDialog() == DialogResult.OK)
				{
					this.FONT_DATA_Main = new byte[FontDataSize];
					this.FontLoad(Patch_ResolveFontPath(), ref this.FONT_DATA_Main);
					//Patch_TrySynthesizeYo();   // БЛОК 1c (Q4a): синтез не сработал на реальном полотне - вместо него замена Ё->Е в Patch_BoardText
					this.ChangeSettingGraphString();
					TabloPatch.WebServer.PassHash = this._settingForm.cfg.WebPassHash;
					try
					{
						string subkey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
						RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(subkey);
						if (registryKey != null)
						{
							if (this._settingForm.cfg.AutoStart)
							{
								registryKey.SetValue("BoardPlayer", "\"" + Application.ExecutablePath + "\"");
							}
							else
							{
								registryKey.DeleteValue("BoardPlayer", false);
							}
							registryKey.Close();
						}
					}
					catch
					{
					}
					// БЛОК 4, F1-12: HKLM-ветка сначала открывается ТОЛЬКО НА ЧТЕНИЕ — раньше
					// writable-open без прав админа бросал исключение, и старая запись автозапуска
					// молча оставалась. Если запись есть: на Vista+ удаляем разовым запуском
					// reg.exe с UAC, на XP — обычной попыткой (с честным логом при неудаче).
					try
					{
						RegistryKey oldKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", false);
						bool oldVal = false;
						if (oldKey != null)
						{
							oldVal = (oldKey.GetValue("BoardPlayer") != null);
							oldKey.Close();
						}
						if (oldVal)
						{
							if (Environment.OSVersion.Version.Major >= 6)
							{
								try
								{
									System.Diagnostics.ProcessStartInfo psiReg = new System.Diagnostics.ProcessStartInfo("reg.exe", "delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run\" /v BoardPlayer /f");
									psiReg.Verb = "runas";
									psiReg.UseShellExecute = true;
									System.Diagnostics.Process.Start(psiReg);
									TabloPatch.SerialPortLog.Write("АВТОЗАПУСК: найдена старая запись в HKLM — запрошено удаление с правами администратора (UAC)");
								}
								catch (Exception exReg)
								{
									TabloPatch.SerialPortLog.Write("АВТОЗАПУСК: не удалось удалить старую запись HKLM через UAC: " + exReg.Message);
								}
							}
							else
							{
								try
								{
									RegistryKey wKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
									if (wKey != null)
									{
										wKey.DeleteValue("BoardPlayer", false);
										wKey.Close();
									}
								}
								catch (Exception exReg2)
								{
									TabloPatch.SerialPortLog.Write("АВТОЗАПУСК: старая запись HKLM не удалена (нужны права администратора): " + exReg2.Message);
								}
							}
						}
					}
					catch
					{
					}
					if (this.thfBackground != null)
					{
						this.thfBackground.BackColor = this._settingForm.cfg.BackColor;
						this.thfBackground.Left = this._settingForm.cfg.Left;
						this.thfBackground.Top = this._settingForm.cfg.Top;
						this.thfBackground.TopMost = this._settingForm.cfg.TopWindow;
					}
					if (this.vpf != null)
					{
						this.vpf.BackColor = this._settingForm.cfg.BackColor;
						this.vpf.Left = this._settingForm.cfg.Left;
						this.vpf.Top = this._settingForm.cfg.Top;
						this.vpf.TopMost = this._settingForm.cfg.TopWindow;
					}
					if (this.thf != null)
					{
						this.thf.BackColor = this._settingForm.cfg.BackColor;
						this.thf.Left = this._settingForm.cfg.Left;
						this.thf.Top = this._settingForm.cfg.Top;
						this.thf.TopMost = this._settingForm.cfg.TopWindow;
					}
					if (this.thfPrompt != null)
					{
						this.thfPrompt.BackColor = this._settingForm.cfg.BackColor;
						this.thfPrompt.Left = this._settingForm.cfg.Left;
						this.thfPrompt.Top = this._settingForm.cfg.Top;
						this.thfPrompt.TopMost = this._settingForm.cfg.TopWindow;
					}
					if (this._WhistleBeep != null)
					{
						this._WhistleBeep.PrepareSound(Patch_AudioPath(this._settingForm.cfg.WhistleBeepFile));
					}
					if (this._GetGateBeep != null)
					{
						this._GetGateBeep.PrepareSound(Patch_AudioPath(this._settingForm.cfg.GetGateBeepFile));
					}
					if (this._GameStatrBeep != null)
					{
						this._GameStatrBeep.PrepareSound(Patch_AudioPath(this._settingForm.cfg.StartGameBeepFile));
					}
					if (this._BeforeStartPlayer != null)
					{
						this._BeforeStartPlayer.PrepareSound(Patch_AudioPath(this._settingForm.cfg.BeepFile));
					}
					if (this._GameMode != frmMain.gameMode.GameStarted)
					{
						this.Init_gameVars();
					}
					if (this._GameMode != frmMain.gameMode.Timeout)
					{
						this.Init_timeoutVars();
					}
					if (this._GameMode != frmMain.gameMode.Break)
					{
						this.Init_breakVars();
						TabloPatch.PatchEntry.Init(null);
						this.Patch_SyncGuardFromSettings();
						this.KeyPreview = true;
					}
					// БЛОК 4, F8-09: GC.Collect() удалён (фриз UI после закрытия настроек)
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message);
			}
			finally
			{
				if (this._settingForm != null)   // БЛОК 3b: не падаем, если форма так и не создалась
				{
					this._settingForm.Hide();
				}
			}
		}

		// Token: 0x06000095 RID: 149 RVA: 0x0000BBEC File Offset: 0x00009DEC
		private void but_ScriptPlay_Click(object sender, EventArgs e)
		{
			Button button = (Button)sender;
			if (button.Text.Contains("Запуск"))
			{
				this.Cmd_ScriptPlay();
			}
			else
			{
				this.Cmd_ScriptStop();
			}
			this.statusShow();
		}

		// Token: 0x06000096 RID: 150 RVA: 0x0000BC28 File Offset: 0x00009E28
		private void but_BeforeStart_Click(object sender, EventArgs e)
		{
			Button button = (Button)sender;
			if (button.Text == "Запустить")
			{
				this.Cmd_BeforeStart();
			}
			else
			{
				this.Cmd_BeforeStop();
			}
			this.statusShow();
		}

		// Token: 0x06000097 RID: 151 RVA: 0x000024B4 File Offset: 0x000006B4
		private void but_StopGame_Click(object sender, EventArgs e)
		{
			this.Cmd_Stop();
			Button button = (Button)sender;
			this.statusShow();
		}

		// Token: 0x06000098 RID: 152 RVA: 0x000024C9 File Offset: 0x000006C9
		private void but_Timeout_Click(object sender, EventArgs e)
		{
			this.Cmd_Timeout(0, 0, true);
			Button button = (Button)sender;
			this.statusShow();
		}

		// Token: 0x06000099 RID: 153 RVA: 0x000024E1 File Offset: 0x000006E1
		private void but_BreakGame_Click(object sender, EventArgs e)
		{
			this.Cmd_BreakGame();
			Button button = (Button)sender;
			this.statusShow();
		}

		// Token: 0x0600009A RID: 154 RVA: 0x000024F6 File Offset: 0x000006F6
		private void but_StartGame_Click(object sender, EventArgs e)
		{
			this.Cmd_Start();
			Button button = (Button)sender;
			this.statusShow();
		}

		// Token: 0x0600009B RID: 155 RVA: 0x0000250B File Offset: 0x0000070B
		private void but_ResetGame_Click(object sender, EventArgs e)
		{
			// БЛОК 4, F6-12: нативная кнопка «Сброс игры» требует подтверждения —
			// слишком дорогая ошибка нажать её вместо «Стоп». Пульт и веб идут через
			// Cmd_ResetGame напрямую, как и раньше (протокол пульта не изменён, Q5;
			// веб-панель имеет собственное двойное подтверждение).
			if (MessageBox.Show("Выполнить ПОЛНЫЙ сброс матча?\r\n\r\nОбнулятся: счёт, период, игровое время, все удаления и тайм-ауты обеих команд; архив времени будет очищен.", "Табло — сброс матча", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
			{
				return;
			}
			this.Cmd_ResetGame();
			Button button = (Button)sender;
			this.statusShow();
		}

		// Token: 0x0600009C RID: 156 RVA: 0x00002520 File Offset: 0x00000720
		private void but_TimeReset_Click(object sender, EventArgs e)
		{
			this.Cmd_TimeReset();
			Button button = (Button)sender;
			this.statusShow();
		}

		// Token: 0x0600009D RID: 157 RVA: 0x00002535 File Offset: 0x00000735
		private void but_ShowPrompt_Click(object sender, EventArgs e)
		{
			if (this._TabloMode == frmMain.tabloMode.prompt)
			{
				this.Cmd_ShowGame();
				return;
			}
			this.Cmd_ShowPrompt();
		}

		// Token: 0x0600009E RID: 158 RVA: 0x0000BC64 File Offset: 0x00009E64
	
		private void Cmd_Start()
		{
			this.Patch_PurgeExpired();
			if (this._GameMode == frmMain.gameMode.Timeout)
			{
				this.EndTimeout_OnManual();
				this._GameMode = frmMain.gameMode.GameStoped;
			}
			if (this._GameMode == frmMain.gameMode.GameStoped)
			{
				if (this._gameOver)
				{
					this._gameOver = false;
					this.Init_gameVars();
				}
				this._storedTime = TimeSpan.FromMilliseconds((double)this._gameTime.Milliseconds);
				// БЛОК 2, F2-01: фиксируем базу ПЕРЕД стартом — иначе в время попала бы пауза
				this.Patch_GameTimeMarkStart();
				this._gameTimer.Start();
				this._videoEnabled = false;
			}
			if (this._GameMode == frmMain.gameMode.Break)
			{
				// БЛОК 4, F6-09: старт во время перерыва больше не игнорируется молча —
				// причина видна в статус-строке и в логе.
				TabloPatch.SerialPortLog.Write("СТАРТ: отклонён — идёт перерыв. Сначала остановите перерыв (кнопка СТОП / соответствующая команда пульта).");
				this.Patch_StatusHint("Идёт перерыв — старт возможен только после остановки перерыва");
				return;
			}
			this._videoEnabled = false;
			this._TabloMode = frmMain.tabloMode.game;
			this._GameMode = frmMain.gameMode.GameStarted;
			this.TabloMode();
			this.statusShow();
		}

		// Token: 0x0600009F RID: 159 RVA: 0x0000BCFC File Offset: 0x00009EFC
		 	private void Cmd_Stop()
 	{
 			this._patchTimeoutOwner = 0;
 			bool flag = this._GameMode == frmMain.gameMode.GameStarted;
 		this._gameTimer.Stop();
			if (this._GameMode == frmMain.gameMode.Timeout)
			{
				this.EndTimeout_OnManual();
			}
			if (this._GameMode == frmMain.gameMode.Break)
			{
				this.EndBreak_OnManual();
			}
			this._TabloMode = frmMain.tabloMode.game;
			this._GameMode = frmMain.gameMode.GameStoped;
			this.TabloMode();
			this.statusShow();
			if (flag)
			{
				try
				{
					// БЛОК 4, F9-05: список архива из кэша
					ListBox listBox = this.Patch_GetArchiveList();
					if (listBox != null)
					{
						TimeSpan t = this._gameTotalTime - this._gameTime;
						if (t < TimeSpan.Zero)
						{
							t = TimeSpan.Zero;
						}
						int num = (int)Math.Ceiling(t.TotalSeconds);
						string text = (num / 60).ToString("D2") + ":" + (num % 60).ToString("D2");
						string text2 = "";
						foreach (object obj in this.lB_DelPeople_A.Items)
						{
							text2 = text2 + ((text2.Length > 0) ? "; " : "") + (string)obj;
						}
						string text3 = "";
						foreach (object obj2 in this.lB_DelPeople_B.Items)
						{
							text3 = text3 + ((text3.Length > 0) ? "; " : "") + (string)obj2;
						}
						int num2 = (listBox.Tag is int) ? ((int)listBox.Tag) : 0;
						num2++;
						listBox.Tag = num2;
						// ПУНКТ 12: слепок стопа дополнен НОМЕРОМ ПЕРИОДА и СЧЁТОМ.
						// Новые поля дописаны В КОНЕЦ строки, поэтому старые записи
						// (из match_state.xml прежних версий) разбираются как раньше.
						listBox.Items.Add(string.Concat(new object[]
						{
							"№",
							num2,
							" | ",
							text,
							" | A: ",
							text2,
							" | B: ",
							text3,
							" | П: ",
							(int)this.nUD_Period.Value,
							" | С: ",
							(int)this.nUD_Score_1.Value,
							":",
							(int)this.nUD_Score_2.Value
						}));
						// ПУНКТ 12: архив хранит 6 ПОСЛЕДНИХ стопов (было 40) — столько же
						// показывает веб-панель. Старые записи вытесняются новыми.
						while (listBox.Items.Count > 6)
						{
							listBox.Items.RemoveAt(0);
						}
					}
				}
				catch
				{
				}
			}
		}

		// Token: 0x060000A0 RID: 160 RVA: 0x0000BFAC File Offset: 0x0000A1AC
		private void Cmd_ResetGame()
		{
			TabloPatch.PatchEntry.Core.MasterReset();
			// БЛОК 2, F6-04: мастер-сброс удаляет и файл автосохранения состояния матча
			this.Patch_ClearSavedState();
			this.TimeOutCounter_L = 0;
			this.TimeOutCounter_R = 0;
			// БЛОК 4, F6-16: мастер-сброс чистит ТАКЖЕ архив времени, признак ручной
			// установки овертайма и скорость хода часов — не оставляет «хвостов» матча.
			try
			{
				ListBox lbArchReset = this.Patch_GetArchiveList();
				if (lbArchReset != null)
				{
					lbArchReset.Items.Clear();
					lbArchReset.Tag = 0;
				}
			}
			catch
			{
			}
			this.gB_Tablo.Tag = null;
			try
			{
				ComboBox cbSpReset = this.Patch_GetCbSpeed();
				if (cbSpReset != null && cbSpReset.Items.Contains("1"))
				{
					cbSpReset.SelectedItem = "1";
				}
			}
			catch
			{
			}
			// БЛОК 4, F6-05: обнуление счётчиков тайм-аутов фиксируется в логе
			TabloPatch.SerialPortLog.Write("МАСТЕР-СБРОС: счёт/период/время/удаления обнулены, счётчики тайм-аутов = 0/0, архив времени очищен, скорость = 1");
			this.Patch_SyncGuardFromSettings();
			this.Cmd_Stop();
			this.nUD_Period.Value = 1m;
			this.Cmd_TimeReset();
			this.Clear_del();
			this.nUD_Score_1.Value = 0m;
			this.nUD_Score_2.Value = 0m;
			this._gameOver = true;
			this.TabloMode();
			this.statusShow();
		}

		// Token: 0x060000A1 RID: 161 RVA: 0x0000C010 File Offset: 0x0000A210
		private void Cmd_TimeReset()
		{
			this._gameTime = TimeSpan.Zero;
			if (this._settingForm != null)
			{
				int num = 1;
				try
				{
					num = (int)this.nUD_Period.Value;
				}
				catch
				{
				}
				bool flag = object.Equals(this.gB_Tablo.Tag, "otmanual");
				if (num >= 4)
				{
					if (!flag)
					{
						// БЛОК 4, F9-10/F8-07: единая точка чтения овертайма (кэш + честный лог)
						int otSec = this.Patch_OvertimeSecFromFiles();
						TimeSpan timeSpan = (otSec > 0) ? TimeSpan.FromSeconds((double)otSec) : TimeSpan.FromMinutes(5.0);
						this._gameTotalTime = ((timeSpan.TotalSeconds > 0.0) ? timeSpan : this._settingForm.cfg.GameTime);
					}
				}
				else
				{
					this._gameTotalTime = this._settingForm.cfg.GameTime;
				}
				this._gameStroke = this._settingForm.cfg.ForwardStrokeGame;
				if (this._gameTotalTime.TotalSeconds < 1.0)
				{
					// БЛОК 4, F6-15: подстановка больше не молчаливая
					TabloPatch.Diag.LogOnce("Длина периода < 1 с", "ВРЕМЯ: длина периода из настроек/овертайма меньше 1 с — подставлено 1:00. Проверьте «Настройки -> Время периода» и tablo_overtime.txt");
					this._gameTotalTime = TimeSpan.FromMinutes(1.0);
				}
			}
			this._timeoutTime = TimeSpan.Zero;
			this._breakTime = TimeSpan.Zero;
			this.TabloMode();
			this.statusShow();
		}

		// Token: 0x060000A2 RID: 162 RVA: 0x0000C1A4 File Offset: 0x0000A3A4
		private void Cmd_Timeout(int TL, int TR, bool buttonpress)
		{
			// ===== БЛОК 2, Q6: тайм-аут во время ИДУЩЕГО периода запрещён ВСЕМ источникам =====
			// (пульт, веб и нативная кнопка идут через этот метод — защита одна на всех).
			// Режим бога (Q16) — исключение. Отмена УЖЕ идущего тайм-аута (режим Timeout)
			// по-прежнему разрешена. Пульт: протокол/DLL не тронуты (Q5), отклонённая команда
			// логируется, счётчики тайм-аутов НЕ изменяются.
			if (this._GameMode == frmMain.gameMode.GameStarted && !TabloPatch.WebServer.GodMode)
			{
				TabloPatch.SerialPortLog.Write("ЗАПРЕТ(Q6): ТАЙМ-АУТ отклонён — время периода идёт. Сначала СТОП (или включите режим бога). buttonpress=" + (buttonpress ? "1" : "0"));
				return;
			}
			if (!buttonpress)
			{
				this.TimeOutCounter_L = TL;
				this.TimeOutCounter_R = TR;
				// БЛОК 4, F6-05: установка счётчиков тайм-аутов командой — в лог
				TabloPatch.SerialPortLog.Write("СЧЁТЧИКИ ТАЙМ-АУТОВ: дома=" + TL + " гости=" + TR + " (установлено командой пульта/веба)");
			}
			if (this._GameMode == frmMain.gameMode.Timeout)
			{
				this.Cmd_Stop();
			}
			 		else
 		{
 			if (this._GameMode == frmMain.gameMode.GameStarted)
 			{
 				this._gameTimer.Stop();
 			}
 			this.Init_timeoutVars();
 			this._videoEnabled = true;
 			this._timeoutTimer.Start();
				this._TabloMode = frmMain.tabloMode.game;
				this._GameMode = frmMain.gameMode.Timeout;
				this.TabloMode();
			}
			this.statusShow();
		}

		// Token: 0x060000A3 RID: 163 RVA: 0x0000C208 File Offset: 0x0000A408
		private void Cmd_BreakGame()
		{
			// ===== БЛОК 2, Q6: перерыв во время ИДУЩЕГО периода запрещён ВСЕМ источникам =====
			// (пульт, веб, нативная кнопка). Режим бога — исключение. Отмена УЖЕ идущего
			// перерыва (режим Break) по-прежнему разрешена. Авто-переход периода (Q9)
			// не затронут: EndGame_OnTimer сначала делает Cmd_Stop(), режим уже GameStoped.
			if (this._GameMode == frmMain.gameMode.GameStarted && !TabloPatch.WebServer.GodMode)
			{
				TabloPatch.SerialPortLog.Write("ЗАПРЕТ(Q6): ПЕРЕРЫВ отклонён — время периода идёт. Сначала СТОП (или включите режим бога).");
				return;
			}
			if (this._GameMode == frmMain.gameMode.Break)
			{
				this.Cmd_Stop();
			}
			 		else
 		{
 			if (this._GameMode == frmMain.gameMode.GameStarted)
 			{
 				this._gameTimer.Stop();
 			}
 			this.Init_breakVars();
 			this._videoEnabled = true;
 			this._breakTimer.Start();
				this._TabloMode = frmMain.tabloMode.game;
				this._GameMode = frmMain.gameMode.Break;
				this.TabloMode();
			}
			this.statusShow();
		}

		// Token: 0x060000A4 RID: 164 RVA: 0x0000254C File Offset: 0x0000074C
		private void Cmd_ShowPrompt()
		{
			this._TabloMode = frmMain.tabloMode.prompt;
			this._videoEnabled = false;
			this.TabloMode();
			this.statusShow();
		}

		// Token: 0x060000A5 RID: 165 RVA: 0x00002568 File Offset: 0x00000768
		private void Cmd_ShowGame()
		{
			this._TabloMode = frmMain.tabloMode.game;
			if (this._GameMode == frmMain.gameMode.Break || this._GameMode == frmMain.gameMode.Timeout)
			{
				this._videoEnabled = true;
			}
			this.TabloMode();
			this.statusShow();
		}

		// Token: 0x060000A6 RID: 166 RVA: 0x00002596 File Offset: 0x00000796
		private string _patchVideoShownMode;   // БЛОК 4, F6-16: видео открывается ОДИН раз при входе в режим

		private void Cmd_ShowVideo()
		{
			// БЛОК 4, F6-16: таймеры перерыва/тайм-аута вызывают Cmd_ShowVideo КАЖДУЮ
			// секунду — повторные вызовы в том же режиме игнорируются (табло не
			// «дёргается», статус не перерисовывается). Если плеер закрыт (vpf == null),
			// повторный вход разрешён — как и раньше.
			string modeKey = this._GameMode.ToString();
			if (this._TabloMode == frmMain.tabloMode.video && this.vpf != null && modeKey == this._patchVideoShownMode)
			{
				return;
			}
			this._patchVideoShownMode = modeKey;
			this._TabloMode = frmMain.tabloMode.video;
			this.TabloMode();
			this.statusShow();
		}

		// Token: 0x060000A7 RID: 167 RVA: 0x0000C258 File Offset: 0x0000A458
		private void Cmd_BeforeStart()
		{
			if (this._TabloMode == frmMain.tabloMode.prompt)
			{
				if (!this._beforeTimerEnabled)
				{
					if (this._settingForm != null)
					{
						this._beforeTime = TimeSpan.Zero;
						this._beforeTotalTime = this._settingForm.cfg.TimeBeforeStart;
						this._beforeStroke = false;
						this._beforeTime_1 = this._settingForm.cfg.TimeBeforeFlash;
						this._beforeAudio_1 = false;
						this._beforeTime_2 = this._settingForm.cfg.TimeFirstBeep;
						this._beforeAudio_2 = true;
						this._beforeTime_3 = this._settingForm.cfg.TimeSecondBeep;
						this._beforeAudio_3 = true;
						this._beforeTime_4 = TimeSpan.Zero;
						this._beforeAudio_4 = false;
						this._beforeTime_5 = TimeSpan.Zero;
						this._beforeAudio_5 = false;
						this._beforeTime_6 = TimeSpan.Zero;
						this._beforeAudio_6 = false;
						this._beforeTime_7 = TimeSpan.Zero;
						this._beforeAudio_7 = false;
					}
					else
					{
						this._beforeTime = TimeSpan.Zero;
						this._beforeTotalTime = TimeSpan.Zero;
						this._beforeStroke = false;
						this._beforeTime_1 = TimeSpan.Zero;
						this._beforeAudio_1 = false;
						this._beforeTime_2 = TimeSpan.Zero;
						this._beforeAudio_2 = false;
						this._beforeTime_3 = TimeSpan.Zero;
						this._beforeAudio_3 = false;
						this._beforeTime_4 = TimeSpan.Zero;
						this._beforeAudio_4 = false;
						this._beforeTime_5 = TimeSpan.Zero;
						this._beforeAudio_5 = false;
						this._beforeTime_6 = TimeSpan.Zero;
						this._beforeAudio_6 = false;
						this._beforeTime_7 = TimeSpan.Zero;
						this._beforeAudio_7 = false;
					}
					this._showPromptTime_flash1 = false;
					this._showPromptTime_flash2 = false;
					this.but_BeforeStart.Text = "Остановить";
					this._patchBeforeRef = TimeSpan.FromTicks(DateTime.Now.Ticks);   // БЛОК 4, F6-16
					this._beforeTimerEnabled = true;
				}
				this.TabloMode();
			}
			this.statusShow();
		}

		// Token: 0x060000A8 RID: 168 RVA: 0x0000C438 File Offset: 0x0000A638
		private void Cmd_BeforeStop()
		{
			if (this._TabloMode == frmMain.tabloMode.prompt)
			{
				if (this._beforeTimerEnabled && MessageBox.Show("Остановить отложенный старт?", "Остановка отложенного старта", MessageBoxButtons.YesNo) == DialogResult.Yes)
				{
					this._beforeTimerEnabled = false;
					this._beforeTime = TimeSpan.Zero;
					this.but_BeforeStart.Text = "Запустить";
					if (this.gs_BeforeGameTime != null)
					{
						this.gs_BeforeGameTime.SynchString = null;
						this.gs_BeforeGameTime.EffectIndex = Effects.Stop;
					}
				}
				this.TabloMode();
			}
			this.statusShow();
		}

		// Token: 0x060000A9 RID: 169 RVA: 0x0000C4B8 File Offset: 0x0000A6B8
		private void Cmd_ScriptPlay()
		{
			if (this.gs_Main == null)
			{
				return;
			}
			if (!this.gs_Main.StartScript)
			{
				// БЛОК 4, F6-13: честные сообщения — надпись «Остановка сценария» появляется
				// ТОЛЬКО когда сценарий действительно запущен. Раньше любая ошибка молча
				// проглатывалась, а кнопка всё равно переключалась, вводя оператора в заблуждение.
				try
				{
					if (this.cLB_ScriptFiles.SelectedIndex <= -1)
					{
						TabloPatch.SerialPortLog.Write("СЦЕНАРИЙ: запуск отклонён — в списке не выбран сценарий");
						MessageBox.Show("Сначала выберите сценарий в списке.", "Табло — сценарии", MessageBoxButtons.OK, MessageBoxIcon.Information);
					}
					else
					{
						string scriptFile = this.cLB_ScriptFiles.Items[this.cLB_ScriptFiles.SelectedIndex].ToString();
						StringScriptCollection stringScriptCollection = new StringScriptCollection();
						if (stringScriptCollection.LoadScript(scriptFile))
						{
							this._videoEnabled = false;
							this.gs_Main.TwoLines = false;
							this.gs_Main.ShowTablo4String = false;
							this.gs_Main.ScriptCollection = stringScriptCollection;
							this.gs_Main.StartScript = true;
							if (this._TabloMode != frmMain.tabloMode.game)
							{
								this._TabloMode = frmMain.tabloMode.game;
								this._GameMode = frmMain.gameMode.GameStoped;
							}
							this.but_ScriptPlay.Text = "Остановка сценария";
						}
						else
						{
							TabloPatch.SerialPortLog.Write("СЦЕНАРИЙ: файл не загружен: " + scriptFile);
							MessageBox.Show("Не удалось загрузить файл сценария:\r\n" + scriptFile, "Табло — сценарии", MessageBoxButtons.OK, MessageBoxIcon.Warning);
						}
					}
				}
				catch (Exception exScript)
				{
					TabloPatch.Diag.Note("Запуск сценария", exScript);
					MessageBox.Show("Не удалось запустить сценарий: " + exScript.Message, "Табло — сценарии", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				}
			}
			this.TabloMode();
			this.statusShow();
		}

		// Token: 0x060000AA RID: 170 RVA: 0x0000C5A4 File Offset: 0x0000A7A4
		private void Cmd_ScriptStop()
		{
			if (this.gs_Main == null)
			{
				return;
			}
			this._timerRestore.Stop();
			this.gs_Main.StartScript = false;
			this.gs_Main.ScriptCollection = null;
			this._videoEnabled = true;
			// БЛОК 4, F8-09: GC.Collect() удалён — принудительная полная сборка мусора
			// замораживала UI на сотни миллисекунд в момент остановки сценария.
			this.but_ScriptPlay.Text = "Запуск сценария";
			this.TabloMode();
			this.statusShow();
		}

		// Token: 0x060000AB RID: 171 RVA: 0x0000C608 File Offset: 0x0000A808
		private void EndGame_OnTimer()
		{
			this._gameOver = true;
			this.Cmd_Stop();
			// ===== БЛОК 2, F6-03: восстановление эталона в конце периода — ЯВНО =====
			// Раньше единственный авто-вызов Patch_PeriodEndRestore был привязан к концу
			// бегущей строки (gs_Main_OnAnimatedStop) и при AnimatedString=false не
			// срабатывал НИКОГДА. OnPeriodEnd() идемпотентен в пределах периода —
			// двойной вызов (отсюда и из Patch_ChangePeriod) безопасен. На пульт не влияет:
			// _Controlboard.PeriodEnd() вызывается ниже, как и раньше.
			try
			{
				this.Patch_PeriodEndRestore();
			}
			catch (Exception exr)
			{
				TabloPatch.SerialPortLog.Write("ЭТАЛОН: сбой восстановления в конце периода: " + exr.Message);
			}
			if (this._Controlboard != null)
			{
				this._Controlboard.PeriodEnd();
			}
			if (this._settingForm.cfg.AutoWhistleEnable)
			{
				this.WhistleBeep();
				if (this._settingForm != null && this._settingForm.cfg.WhistleMode)
				{
					int num = (int)this.nUD_Period.Value;
					if (num < 3)
					{
						this.nUD_Period.Value = num + 1;
						this.Cmd_BreakGame();
						this.gB_Tablo.Tag = null;
					}
				}
			}
		}

		// Token: 0x060000AC RID: 172 RVA: 0x000025AB File Offset: 0x000007AB
		private void EndBreak_OnTimer()
		{
			this.Cmd_Stop();
			this.Cmd_TimeReset();
			if (this._settingForm != null && this._settingForm.cfg.AutoWhistleEnable)
			{
				this.WhistleBeep();
			}
		}

		// Token: 0x060000AD RID: 173 RVA: 0x000025D9 File Offset: 0x000007D9
		private void EndTimeout_OnTimer()
		{
			this.Cmd_Stop();
			if (this._settingForm != null && this._settingForm.cfg.AutoWhistleEnable)
			{
				this.WhistleBeep();
			}
		}

		// Token: 0x060000AE RID: 174 RVA: 0x0000C6A4 File Offset: 0x0000A8A4
		private void EndBefore_OnTimer()
		{
			this._beforeTimerEnabled = false;
			this._beforeTime = TimeSpan.Zero;
			this.but_BeforeStart.Text = "Запустить";
			if (this.gs_BeforeGameTime != null)
			{
				this.gs_BeforeGameTime.SynchString = null;
				this.gs_BeforeGameTime.EffectIndex = Effects.Stop;
			}
			this.Cmd_Stop();
			this.Cmd_ShowGame();
		}

		// Token: 0x060000AF RID: 175 RVA: 0x00002601 File Offset: 0x00000801
		private void EndBreak_OnManual()
		{
			this._breakTimer.Stop();
			this.Cmd_TimeReset();
			if (this._Controlboard != null)
			{
				this._Controlboard.BreakEnd();
			}
		}

		// Token: 0x060000B0 RID: 176 RVA: 0x0000C700 File Offset: 0x0000A900
		private void EndTimeout_OnManual()
		{
			this._timeoutTimer.Stop();
			TimeSpan timeSpan = this._timeoutTotalTime - this._timeoutTime;
			if (this._Controlboard != null)
			{
				this._Controlboard.TimeoutEnd((int)timeSpan.TotalSeconds);
			}
		}

		// Token: 0x060000B2 RID: 178 RVA: 0x0000C748 File Offset: 0x0000A948
		private void nUD_Score_1_ValueChanged(object sender, EventArgs e)
		{
			NumericUpDown numericUpDown = (NumericUpDown)sender;
			if (this.gs_LWindow_1 != null)
			{
				this.gs_LWindow_1.Text = ((int)numericUpDown.Value).ToString();
			}
		}

		// Token: 0x060000B3 RID: 179 RVA: 0x0000C784 File Offset: 0x0000A984
		private void nUD_Period_ValueChanged(object sender, EventArgs e)
		{
			NumericUpDown numericUpDown = (NumericUpDown)sender;
			if (this.gs_CentreWindow_small != null)
			{
				this.gs_CentreWindow_small.Text = ((int)numericUpDown.Value).ToString();
			}
		}

		// Token: 0x060000B4 RID: 180 RVA: 0x0000C7C0 File Offset: 0x0000A9C0
		private void nUD_Score_2_ValueChanged(object sender, EventArgs e)
		{
			NumericUpDown numericUpDown = (NumericUpDown)sender;
			if (this.gs_RWindow_1 != null)
			{
				this.gs_RWindow_1.Text = ((int)numericUpDown.Value).ToString();
			}
		}

		// Token: 0x060000B5 RID: 181 RVA: 0x0000C7FC File Offset: 0x0000A9FC
		private void SavingCommandName()
		{
			this._settingForm.cfg.Command_A = this.mtb_Command_A.Text;
			this._settingForm.cfg.TownCommand_A = this.mtb_TownCommand_A.Text;
			this._settingForm.cfg.Command_B = this.mtb_Command_B.Text;
			this._settingForm.cfg.TownCommand_B = this.mtb_TownCommand_B.Text;
			this._settingForm.cfg.Save();
		}

		// Token: 0x060000B6 RID: 182 RVA: 0x0000C888 File Offset: 0x0000AA88
		private void LoadCommandName()
		{
			this.mtb_Command_A.Text = this._settingForm.cfg.Command_A;
			this.mtb_TownCommand_A.Text = this._settingForm.cfg.TownCommand_A;
			this.mtb_Command_B.Text = this._settingForm.cfg.Command_B;
			this.mtb_TownCommand_B.Text = this._settingForm.cfg.TownCommand_B;
		}

		// Token: 0x060000B7 RID: 183 RVA: 0x0000C904 File Offset: 0x0000AB04
		private void mtb_Command_A_TextChanged(object sender, EventArgs e)
		{
			if (this.gs_Main == null)
			{
				return;
			}
			// БЛОК 1c (Q4a): на табло Ё/ё заменяются на Е/е (в шрифте нет этих глифов).
			// Замена ТОЛЬКО для отрисовки: в полях панели, в настройках и в командах пульта текст не меняется.
			this.gs_Main.SetTablo4String(Patch_BoardText(this.mtb_Command_A.Text), Patch_BoardText(this.mtb_TownCommand_A.Text), Patch_BoardText(this.mtb_Command_B.Text), Patch_BoardText(this.mtb_TownCommand_B.Text));
			this.gs_Main.ShowTablo4String = true;
		}

		// Token: 0x060000B8 RID: 184 RVA: 0x00002627 File Offset: 0x00000827
		private void mtb_TownCommand_A_TextChanged(object sender, EventArgs e)
		{
			this.mtb_Command_A_TextChanged(sender, e);
		}

		// Token: 0x060000B9 RID: 185 RVA: 0x00002627 File Offset: 0x00000827
		private void mtb_Command_B_TextChanged(object sender, EventArgs e)
		{
			this.mtb_Command_A_TextChanged(sender, e);
		}

		// Token: 0x060000BA RID: 186 RVA: 0x00002627 File Offset: 0x00000827
		private void mtb_TownCommand_B_TextChanged(object sender, EventArgs e)
		{
			this.mtb_Command_A_TextChanged(sender, e);
		}

		// Token: 0x060000BB RID: 187 RVA: 0x00002631 File Offset: 0x00000831
		private void but_Beep_Click(object sender, EventArgs e)
		{
			this.WhistleBeep();
		}

		// Token: 0x060000BC RID: 188 RVA: 0x0000C960 File Offset: 0x0000AB60
		private void WhistleBeep()
		{
			if (this._WhistleBeep == null)
			{
				this._WhistleBeep = new TAudioPlayer();
				this._WhistleBeep.PrepareSound(Patch_AudioPath(this._settingForm.cfg.WhistleBeepFile));
				return;
			}
			try
			{
				this._WhistleBeep.SoundPlay();
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Ошибка воспроизведения звука свистка");
			}
		}

		// Token: 0x060000BD RID: 189 RVA: 0x0000C9D0 File Offset: 0x0000ABD0
		private void GetGateBeep()
		{
			if (this._GetGateBeep == null)
			{
				return;
			}
			try
			{
				this._GetGateBeep.SoundPlay();
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Ошибка воспроизведения звука взятия ворот");
			}
		}

		// Token: 0x060000BE RID: 190 RVA: 0x0000CA18 File Offset: 0x0000AC18
		private bool GameStatrBeep()
		{
			if (this._GameStatrBeep == null)
			{
				return false;
			}
			try
			{
				this._GameStatrBeep.SoundPlay();
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Ошибка воспроизведения звука начала игры");
				return false;
			}
			return true;
		}

		// Token: 0x060000BF RID: 191 RVA: 0x00002639 File Offset: 0x00000839
		private void _GameStatrBeep_OnAudioPlayerStop(object sender, EventArgs e)
		{
			if (this._settingForm.cfg.WhistleMode)
			{
				// БЛОК 2, F2-01: фиксируем базу перед стартом
				this.Patch_GameTimeMarkStart();
				this._gameTimer.Start();
			}
		}

		// Token: 0x060000C0 RID: 192 RVA: 0x0000CA68 File Offset: 0x0000AC68
		private void BeforeStartPlayer()
		{
			if (this._BeforeStartPlayer == null)
			{
				this._BeforeStartPlayer = new TAudioPlayer();
				this._BeforeStartPlayer.PrepareSound(Patch_AudioPath(this._settingForm.cfg.BeepFile));
				return;
			}
			try
			{
				this._BeforeStartPlayer.SoundPlay();
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Ошибка воспроизведения звука отложенного старта");
			}
		}

		// Token: 0x060000C1 RID: 193 RVA: 0x0000CAD8 File Offset: 0x0000ACD8
		private void печатьСписковToolStripMenuItem_Click(object sender, EventArgs e)
		{
			PrintDialog printDialog = new PrintDialog();
			PrintDocument printDocument = new PrintDocument();
			printDocument.PrintPage += this.pDoc_PrintPage;
			printDialog.Document = printDocument;
			printDialog.UseEXDialog = true;
			if (printDialog.ShowDialog() == DialogResult.OK)
			{
				printDocument.Print();
			}
		}

		// Token: 0x060000C2 RID: 194 RVA: 0x0000CB20 File Offset: 0x0000AD20
		private void pDoc_PrintPage(object sender, PrintPageEventArgs e)
		{
			Graphics graphics = e.Graphics;
			Font font = new Font("Arial", 14f, FontStyle.Regular);
			SolidBrush brush = new SolidBrush(Color.Black);
			int num = 0;
			int num2 = (int)((double)font.GetHeight() * 1.5);
			if (this.cLB_ScriptFiles.Items.Count > 0)
			{
				graphics.DrawString("Список сценариев", font, brush, 10f, (float)num);
				num += num2;
				int num3 = 1;
				foreach (object obj in this.cLB_ScriptFiles.Items)
				{
					string path = (string)obj;
					string s = num3.ToString() + ". " + Path.GetFileNameWithoutExtension(path);
					graphics.DrawString(s, font, brush, 0f, (float)num);
					num += num2;
					num3++;
				}
			}
			if (this.cLB_VideoFiles.Items.Count > 0)
			{
				graphics.DrawString("Список видео файлов проигрываемых во время перерыва", font, brush, 10f, (float)num);
				num += num2;
				int num4 = 1;
				foreach (object obj2 in this.cLB_VideoFiles.Items)
				{
					string path2 = (string)obj2;
					string s2 = num4.ToString() + ". " + Path.GetFileNameWithoutExtension(path2);
					graphics.DrawString(s2, font, brush, 0f, (float)num);
					num += num2;
					num4++;
				}
			}
			if (this.cLB_TimeoutVideoFiles.Items.Count > 0)
			{
				graphics.DrawString("Список видео файлов проигрываемых во время таймаута", font, brush, 10f, (float)num);
				num += num2;
				int num5 = 1;
				foreach (object obj3 in this.cLB_TimeoutVideoFiles.Items)
				{
					string path3 = (string)obj3;
					string s3 = num5.ToString() + ". " + Path.GetFileNameWithoutExtension(path3);
					graphics.DrawString(s3, font, brush, 0f, (float)num);
					num += num2;
					num5++;
				}
			}
		}

		// Token: 0x060000C3 RID: 195 RVA: 0x0000CD8C File Offset: 0x0000AF8C
		private void Clear_del()
		{
			this.lB_DelPeople_A.Items.Clear();
			this.lB_DelPeople_B.Items.Clear();
			this.ShowPenalty(0, this.lB_DelPeople_A, this.gs_LWindow_2);
			this.ShowPenalty(1, this.lB_DelPeople_A, this.gs_LWindow_3);
			this.ShowPenalty(0, this.lB_DelPeople_B, this.gs_RWindow_2);
			this.ShowPenalty(1, this.lB_DelPeople_B, this.gs_RWindow_3);
		}

		// Token: 0x060000C4 RID: 196 RVA: 0x0000CE08 File Offset: 0x0000B008
		private void Add_del(ListBox lb, string s)
		{
			try
			{
				string text = (s == null) ? "" : s.Trim();
				if (text.Length >= 2 && char.IsDigit(text[0]) && char.IsDigit(text[1]) && lb != null)
				{
					int num = int.Parse(text.Substring(0, 2));
					int[] array = lb.Tag as int[];
					if (array != null && array.Length == 6)
					{
						for (int i = 0; i < 2; i++)
						{
							if (array[i * 3] == num && array[i * 3 + 1] == -2)
							{
								bool flag = false;
								for (int j = 0; j < lb.Items.Count; j++)
								{
									string text2 = lb.Items[j] as string;
									if (text2 != null && text2.Length >= 2 && text2.Substring(0, 2) == text.Substring(0, 2))
									{
										flag = true;
										break;
									}
								}
								if (flag)
								{
									return;
								}
								array[i * 3] = -1;
							}
						}
					}
				}
			}
			catch
			{
			}
			if (s == null)
			{
				return;
			}
			s = s.Trim();
			s = s.Trim();
			if (s.Length <= 2)
			{
				TimeSpan timeSpan = TimeSpan.FromMinutes(2.0);
				if (this._settingForm != null && this._settingForm.cfg != null)
				{
					timeSpan = this._settingForm.cfg.TimeBeforeFlash;
				}
				if (timeSpan.TotalSeconds < 1.0)
				{
					timeSpan = TimeSpan.FromMinutes(2.0);
				}
				s = string.Concat(new string[]
				{
					s,
					" ",
					timeSpan.Minutes.ToString("D2"),
					":",
					timeSpan.Seconds.ToString("D2")
				});
			}
			if (s.Length < 8)
			{
				return;
			}
			if (!char.IsDigit(s[0]) || !char.IsDigit(s[1]))
			{
				return;
			}
			short num2;
			try
			{
				num2 = Convert.ToInt16(s.Substring(0, 2));
			}
			catch
			{
				return;
			}
			if (num2 == 0)
			{
				return;
			}
			bool flag2 = false;
			foreach (object obj in lb.Items)
			{
				string text3 = obj as string;
				if (text3 != null && text3.Length >= 2 && Convert.ToInt16(text3.Substring(0, 2)) == num2)
				{
					lb.Items[lb.Items.IndexOf(obj)] = s;
					flag2 = true;
					break;
				}
			}
			if (!flag2)
			{
				lb.Items.Add(s);
			}
			if (lb == this.lB_DelPeople_A)
			{
				this.ShowPenalty(0, this.lB_DelPeople_A, this.gs_LWindow_2);
				this.ShowPenalty(1, this.lB_DelPeople_A, this.gs_LWindow_3);
				return;
			}
			this.ShowPenalty(0, this.lB_DelPeople_B, this.gs_RWindow_2);
			this.ShowPenalty(1, this.lB_DelPeople_B, this.gs_RWindow_3);
		}

		// Token: 0x060000C5 RID: 197 RVA: 0x00002658 File Offset: 0x00000858
		private void but_DelPeople_A_Add_Click(object sender, EventArgs e)
		{
			this.Add_del(this.lB_DelPeople_A, this.mTB_DelPeople_A.Text);
		}

		// Token: 0x060000C6 RID: 198 RVA: 0x00002671 File Offset: 0x00000871
		private void but_DelPeople_B_Add_Click(object sender, EventArgs e)
		{
			this.Add_del(this.lB_DelPeople_B, this.mTB_DelPeople_B.Text);
		}

		// Token: 0x060000C7 RID: 199 RVA: 0x0000268A File Offset: 0x0000088A
		private void mTB_DelPeople_A_KeyPress(object sender, KeyPressEventArgs e)
		{
			if (e.KeyChar == '\r')
			{
				this.but_DelPeople_A_Add_Click(this.but_DelPeople_A_Add, new EventArgs());
				e.Handled = true;
			}
		}

		// Token: 0x060000C8 RID: 200 RVA: 0x000026AE File Offset: 0x000008AE
		private void mTB_DelPeople_B_KeyPress(object sender, KeyPressEventArgs e)
		{
			if (e.KeyChar == '\r')
			{
				this.but_DelPeople_B_Add_Click(this.but_DelPeople_B_Add, new EventArgs());
				e.Handled = true;
			}
		}

		// Token: 0x060000C9 RID: 201 RVA: 0x0000D170 File Offset: 0x0000B370
		private void lB_DelPeople_A_Click(object sender, EventArgs e)
		{
			ListBox listBox = (ListBox)sender;
			if (listBox.SelectedIndex < 0)
			{
				return;
			}
			this.mTB_DelPeople_A.Text = (string)listBox.Items[listBox.SelectedIndex];
		}

		// Token: 0x060000CA RID: 202 RVA: 0x0000D1B0 File Offset: 0x0000B3B0
		private void lB_DelPeople_B_Click(object sender, EventArgs e)
		{
			ListBox listBox = (ListBox)sender;
			if (listBox.SelectedIndex < 0)
			{
				return;
			}
			this.mTB_DelPeople_B.Text = (string)listBox.Items[listBox.SelectedIndex];
		}
		private int Patch_CurrentPeriodNumber()
		{
			try
			{
				return (int)this.nUD_Period.Value;
			}
			catch
			{
				return 1;
			}
		}

		private int Patch_OvertimeSecFromFiles()
		{
			// БЛОК 4, F9-10: значение из кэша (2 с), разбор через TimeSpan.TryParse,
			// «нет файла/битый файл» честно пишется в лог один раз (F8-07).
			return this.Patch_OvertimeSecCached();
		}

		private void Patch_WriteOvertimeFile(int sec)
		{
			try
			{
				TimeSpan ts = TimeSpan.FromSeconds((double)sec);
				string path = Path.Combine(Application.StartupPath, "tablo_overtime.txt");
				// БЛОК 4, F4-03/F8-09: атомарная запись с BOM; кэш значения сбрасывается
				TabloPatch.TextIO.WriteAll(path, ts.ToString() + "\r\n", true);
				this._patchOtCacheSec = -1;
				this._patchOtFileNote = 0;
			}
			catch (Exception ex)
			{
				TabloPatch.Diag.Note("Запись tablo_overtime.txt", ex);
			}
		}
				private string Patch_ExternalSnapshot()
		{
			if (this._GameMode == frmMain.gameMode.Break)
			{
				TimeSpan rem = this._breakTotalTime - this._breakTime;
				if (rem < TimeSpan.Zero)
				{
					rem = TimeSpan.Zero;
				}
				int sec = (int)Math.Ceiling(rem.TotalSeconds);
				return "BRK|" + (sec / 60).ToString("D2") + ":" + (sec % 60).ToString("D2");
			}
			if (this._TabloMode == frmMain.tabloMode.prompt && this._beforeTimerEnabled)
			{
				TimeSpan rem2 = this._beforeTotalTime - this._beforeTime;
				if (rem2 < TimeSpan.Zero)
				{
					rem2 = TimeSpan.Zero;
				}
				int sec2 = (int)Math.Ceiling(rem2.TotalSeconds);
				return "PRE|" + (sec2 / 60).ToString("D2") + ":" + (sec2 % 60).ToString("D2");
			}
			if (this._TabloMode == frmMain.tabloMode.game)
			{
				return "GAME|" + DateTime.Now.ToString("HH:mm:ss");
			}
			return "CLK|" + DateTime.Now.ToString("HH:mm:ss");
		}

		
		private void Patch_SetBeforeTotal(TimeSpan total)
		{
			if (total < TimeSpan.Zero)
			{
				total = TimeSpan.Zero;
			}
			this._beforeTotalTime = total;
			this._beforeTime = TimeSpan.Zero;
			this._patchBeforeRef = TimeSpan.FromTicks(DateTime.Now.Ticks);   // БЛОК 4, F6-16
			if (this._TabloMode != frmMain.tabloMode.prompt)
			{
				this.Cmd_ShowPrompt();
			}
			if (!this._beforeTimerEnabled)
			{
				this.Cmd_BeforeStart();
			}
			this.TabloMode();
		}

			private string Patch_WebState(out string pubJson)
	{
		string mode = "CLK";
		string value = DateTime.Now.ToString("HH:mm:ss");
		string value2 = "";
		if (this._GameMode == frmMain.gameMode.Break)
		{
			mode = "BRK";
			TimeSpan rem = this._breakTotalTime - this._breakTime;
			if (rem < TimeSpan.Zero)
			{
				rem = TimeSpan.Zero;
			}
			int sec = (int)Math.Ceiling(rem.TotalSeconds);
			value = (sec / 60).ToString("D2") + ":" + (sec % 60).ToString("D2");
		}
		else if (this._GameMode == frmMain.gameMode.Timeout)
		{
			mode = "TIMEOUT";
			TimeSpan remG = this._gameTotalTime - this._gameTime;
			if (remG < TimeSpan.Zero)
			{
				remG = TimeSpan.Zero;
			}
			int secG = (int)Math.Ceiling(remG.TotalSeconds);
			value = (secG / 60).ToString("D2") + ":" + (secG % 60).ToString("D2");
			TimeSpan remT = this._timeoutTotalTime - this._timeoutTime;
			if (remT < TimeSpan.Zero)
			{
				remT = TimeSpan.Zero;
			}
			int secT = (int)Math.Ceiling(remT.TotalSeconds);
			value2 = (secT / 60).ToString("D2") + ":" + (secT % 60).ToString("D2");
		}
		else if (this._TabloMode == frmMain.tabloMode.prompt && this._beforeTimerEnabled)
		{
			mode = "PRE";
			TimeSpan rem2 = this._beforeTotalTime - this._beforeTime;
			if (rem2 < TimeSpan.Zero)
			{
				rem2 = TimeSpan.Zero;
			}
			int sec2 = (int)Math.Ceiling(rem2.TotalSeconds);
			value = (sec2 / 60).ToString("D2") + ":" + (sec2 % 60).ToString("D2");
		}
		else if (this._TabloMode == frmMain.tabloMode.game)
		{
			mode = "GAME";
			TimeSpan t = this._gameTotalTime - this._gameTime;
			if (t < TimeSpan.Zero)
			{
				t = TimeSpan.Zero;
			}
			int sec3 = (int)Math.Ceiling(t.TotalSeconds);
			value = (sec3 / 60).ToString("D2") + ":" + (sec3 % 60).ToString("D2");
			int numP = 1;
			try
			{
				numP = (int)this.nUD_Period.Value;
			}
			catch
			{
			}
			bool msWin = (numP == 3 && t.TotalMinutes <= 2.0) || (numP < 3 && t.TotalMinutes <= 1.0);
			if (msWin)
			{
				double rem = t.TotalSeconds;
				int whole = (int)Math.Ceiling(rem);
				int hundredths = 99 - (int)((whole - rem) * 100);
				if (hundredths < 0)
				{
					hundredths = 0;
				}
				if (hundredths > 99)
				{
					hundredths = 99;
				}
				if (whole <= 0)
				{
					hundredths = 0;
				}
				value = (whole / 60).ToString("D2") + ":" + (whole % 60).ToString("D2") + ":" + hundredths.ToString("D2");
			}
		}
		string penA = "";
		foreach (object obj in this.lB_DelPeople_A.Items)
		{
			penA = penA + ((penA.Length > 0) ? "; " : "") + (string)obj;
		}
		string penB = "";
		foreach (object obj2 in this.lB_DelPeople_B.Items)
		{
			penB = penB + ((penB.Length > 0) ? "; " : "") + (string)obj2;
		}
		string spd = "1";
		try
		{
			ComboBox spdCb = this.Patch_GetCbSpeed();   // БЛОК 4, F9-05: из кэша
			if (spdCb != null && spdCb.SelectedItem != null)
			{
				spd = spdCb.SelectedItem.ToString();
			}
		}
		catch
		{
		}
		int periodNum = 1;
		int scoreH = 0;
		int scoreA = 0;
		int gameSec = 0;
		int breakSec = 0;
		int elapsedSec = 0;
		int runFlag = (this._GameMode == frmMain.gameMode.GameStarted) ? 1 : 0;
		try
		{
			periodNum = (int)this.nUD_Period.Value;
			scoreH = (int)this.nUD_Score_1.Value;
			scoreA = (int)this.nUD_Score_2.Value;
			gameSec = (int)this._settingForm.cfg.GameTime.TotalSeconds;
			breakSec = (int)this._settingForm.cfg.BreakTime.TotalSeconds;
			elapsedSec = (int)this._gameTime.TotalSeconds;
		}
		catch
		{
		}
				System.Text.StringBuilder jb = new System.Text.StringBuilder();
		jb.Append("{");
		jb.Append("\"mode\":\"").Append(mode).Append("\",");
		jb.Append("\"value\":\"").Append(value).Append("\",");
		jb.Append("\"value2\":\"").Append(value2).Append("\",");
		jb.Append("\"period\":").Append(((int)this.nUD_Period.Value).ToString()).Append(",");
		jb.Append("\"score_h\":").Append(((int)this.nUD_Score_1.Value).ToString()).Append(",");
		jb.Append("\"score_a\":").Append(((int)this.nUD_Score_2.Value).ToString()).Append(",");
		jb.Append("\"to_h\":").Append(this.TimeOutCounter_L.ToString()).Append(",");
		jb.Append("\"to_a\":").Append(this.TimeOutCounter_R.ToString()).Append(",");
		jb.Append("\"name_h\":\"").Append(JsonEsc(this.mtb_Command_A.Text)).Append("\",");
		jb.Append("\"town_h\":\"").Append(JsonEsc(this.mtb_TownCommand_A.Text)).Append("\",");
		jb.Append("\"name_a\":\"").Append(JsonEsc(this.mtb_Command_B.Text)).Append("\",");
		jb.Append("\"town_a\":\"").Append(JsonEsc(this.mtb_TownCommand_B.Text)).Append("\",");
		jb.Append("\"pen_h\":\"").Append(JsonEsc(penA)).Append("\",");
		jb.Append("\"pen_a\":\"").Append(JsonEsc(penB)).Append("\",");
		jb.Append("\"game_sec\":").Append(((int)this._settingForm.cfg.GameTime.TotalSeconds).ToString()).Append(",");
		jb.Append("\"ot_sec\":").Append(this.Patch_OvertimeSecFromFiles().ToString()).Append(",");
		jb.Append("\"break_sec\":").Append(((int)this._settingForm.cfg.BreakTime.TotalSeconds).ToString()).Append(",");
		jb.Append("\"ref_main\":").Append(TabloPatch.PatchEntry.Core.SavedMainPeriodTimeSec.ToString()).Append(",");
		jb.Append("\"ref_ot\":").Append(TabloPatch.PatchEntry.Core.SavedOvertimeTimeSec.ToString()).Append(",");
		jb.Append("\"ref_lock\":").Append((TabloPatch.PatchEntry.Core.MainTimeMemoryLocked && TabloPatch.PatchEntry.Core.OtTimeMemoryLocked) ? "1" : "0").Append(",");
		jb.Append("\"match_start\":\"").Append(JsonEsc(this._settingForm.cfg.MatchStart)).Append("\",");
		jb.Append("\"speed\":\"").Append(JsonEsc(spd)).Append("\",");
		jb.Append("\"god\":").Append(TabloPatch.WebServer.GodMode ? "1" : "0").Append(",");
		jb.Append("\"run\":").Append((this._GameMode == frmMain.gameMode.GameStarted) ? "1" : "0").Append(",");
		jb.Append("\"elapsed\":").Append(((int)this._gameTime.TotalSeconds).ToString());
		// ПУНКТ 12: архив времени - 6 последних стопов (время матча, активные позиции,
		// счёт, период). Только в полном state.json: витрине /display это не нужно.
		jb.Append(",\"arch\":").Append(this.Patch_ArchiveJson(6));
		jb.Append("}");
		// БЛОК 3, Q15: публичная часть (только то, что видит страница /display):
		// режим, время, период, счёт, названия, удаления, таймауты. Служебных полей нет.
		System.Text.StringBuilder pb = new System.Text.StringBuilder();
		pb.Append("{");
		pb.Append("\"mode\":\"").Append(mode).Append("\",");
		pb.Append("\"value\":\"").Append(value).Append("\",");
		pb.Append("\"value2\":\"").Append(value2).Append("\",");
		pb.Append("\"period\":").Append(periodNum.ToString()).Append(",");
		pb.Append("\"score_h\":").Append(scoreH.ToString()).Append(",");
		pb.Append("\"score_a\":").Append(scoreA.ToString()).Append(",");
		pb.Append("\"to_h\":").Append(this.TimeOutCounter_L.ToString()).Append(",");
		pb.Append("\"to_a\":").Append(this.TimeOutCounter_R.ToString()).Append(",");
		// БЛОК 4, F4-14: витрина /display получает названия, обрезанные до 14 знаков
		// (ширина клетки табло) — длинные имена больше не «наезжают» на соседние зоны.
		// В state.json (панель оператора) остаются ПОЛНЫЕ названия для редактирования.
		pb.Append("\"name_h\":\"").Append(JsonEsc(Patch_Fit(this.mtb_Command_A.Text, 14))).Append("\",");
		pb.Append("\"town_h\":\"").Append(JsonEsc(Patch_Fit(this.mtb_TownCommand_A.Text, 14))).Append("\",");
		pb.Append("\"name_a\":\"").Append(JsonEsc(Patch_Fit(this.mtb_Command_B.Text, 14))).Append("\",");
		pb.Append("\"town_a\":\"").Append(JsonEsc(Patch_Fit(this.mtb_TownCommand_B.Text, 14))).Append("\",");
		pb.Append("\"pen_h\":\"").Append(JsonEsc(penA)).Append("\",");
		pb.Append("\"pen_a\":\"").Append(JsonEsc(penB)).Append("\"");
		pb.Append("}");
		pubJson = pb.ToString();
		return jb.ToString();
	}
		private string JsonEsc(string s)
	{
		if (s == null)
		{
			return "";
		}
		System.Text.StringBuilder sb = new System.Text.StringBuilder();
		char bs = (char)92;
		char q = (char)34;
		foreach (char c in s)
		{
			if (c == bs || c == q)
			{
				sb.Append(bs).Append(c);
			}
			else if (c < (char)32)
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
		private void Patch_ProcessPassResetFile()
		{
			try
			{
				string p = Path.Combine(Application.StartupPath, "passreset.txt");
				if (!File.Exists(p))
				{
					return;
				}
				// БЛОК 4, F8-07: все ветки файла сброса паролей видны в логе (файл security-значимый)
				string content = TabloPatch.TextIO.ReadAllText(p).Trim();
				File.Delete(p);
				int i = content.IndexOf(':');
				if (i <= 0)
				{
					TabloPatch.SerialPortLog.Write("СБРОС ПАРОЛЕЙ: passreset.txt имеет неверный формат (ожидалось «<1|2|3>:<хэш супер-пароля>») — файл удалён, сброс НЕ выполнен");
					return;
				}
				string opt = content.Substring(0, i);
				string hash = content.Substring(i + 1).Trim();
				if (!TabloPatch.WebServer.FixedTimeStrEquals(hash, TabloPatch.DevGuard.SuperHash))
				{
					TabloPatch.SerialPortLog.Write("СБРОС ПАРОЛЕЙ: хэш в passreset.txt НЕ совпал с супер-паролем — сброс НЕ выполнен, файл удалён");
					return;
				}
				if (opt == "1")
				{
					this._settingForm.cfg.DevPassHash = "";
					this._settingForm.cfg.WebPassHash = "";
					this._settingForm.cfg.WebEnable = "0";
					this._settingForm.cfg.WebEnableToken = "";
				}
				else if (opt == "2")
				{
					this._settingForm.cfg.WebPassHash = "";
				}
				else if (opt == "3")
				{
					this._settingForm.cfg.DevPassHash = "";
					this._settingForm.cfg.WebEnable = "0";
					this._settingForm.cfg.WebEnableToken = "";
				}
				else
				{
					TabloPatch.SerialPortLog.Write("СБРОС ПАРОЛЕЙ: неизвестный вариант «" + opt + "» (ожидался 1, 2 или 3) — сброс НЕ выполнен");
					return;
				}
				this._settingForm.cfg.Save();
				TabloPatch.SerialPortLog.Write("СБРОС ПАРОЛЕЙ: выполнен супер-администратором из passreset.txt, вариант " + opt);
			}
			catch
			{
			}
		}

				private void Patch_DevMenu_Click(object sender, EventArgs e)
		{
			Tablo.TConfig cfg = this._settingForm.cfg;
			if (!TabloPatch.DevSession.Remembered)
			{
				if (string.IsNullOrEmpty(cfg.DevPassHash))
				{
					// БЛОК 5: пароль входа НЕ задан — задаётся прямо здесь, в одном
					// диалоге, БЕЗ супер-пароля (супер-администратор не требуется).
					// Диалог сам объясняет ситуацию и повторяется при опечатке.
					bool remNew;
					string created = TabloPatch.DevUi.PromptCreatePassword("Пароль входа в режим разработчика ещё не задан.\nЗадайте его прямо здесь - супер-пароль не требуется.", out remNew);
					if (created == null)
					{
						return;
					}
					cfg.DevPassHash = TabloPatch.WebServer.HashPassword(created);   // БЛОК 4, F7-03: PBKDF2
					cfg.WebEnableToken = TabloPatch.DevGuard.Sign(cfg.DevPassHash, cfg.WebEnable);   // БЛОК 5: подпись включения сразу корректна
					cfg.Save();
					TabloPatch.SerialPortLog.Write("РЕЖИМ РАЗРАБОТЧИКА: пароль входа задан при первом входе (супер-пароль не использовался)");
					if (remNew)
					{
						TabloPatch.DevSession.Grant(TabloPatch.DevSession.ParseIdleMinutes(cfg.DevIdleMinutes));
					}
				}
				else
				{
					// БЛОК 5: при неверном пароле диалог входа показывается снова
					// (раньше окно закрывалось и приходилось открывать меню заново);
					// в диалоге появился флажок «Запомнить пароль».
					while (true)
					{
						bool rem;
						string pass = TabloPatch.DevUi.PromptPasswordRemember("Пароль входа в режим разработчика", out rem);
						if (pass == null)
						{
							return;
						}
						if (TabloPatch.DevGuard.VerifyDev(cfg, pass))   // БЛОК 4, F7-03: PBKDF2/SHA-1 + прозрачный апгрейд
						{
							if (rem)
							{
								TabloPatch.DevSession.Grant(TabloPatch.DevSession.ParseIdleMinutes(cfg.DevIdleMinutes));
							}
							break;
						}
						// БЛОК 5: в журнал пишется состояние хэша - по нему видно,
						// почему пароль не подошёл (не задан/старый формат/повреждён).
						TabloPatch.SerialPortLog.Write("РЕЖИМ РАЗРАБОТЧИКА: неверный пароль входа; состояние хэша: " + TabloPatch.DevGuard.DescribeHashState(cfg.DevPassHash));
						if (MessageBox.Show("Неверный пароль.\n\nПовторить ввод? (Отмена - выход; пароль можно сбросить супер-паролем внутри режима разработчика или файлом passreset.txt)", "Режим разработчика", MessageBoxButtons.YesNo, MessageBoxIcon.Hand) != DialogResult.Yes)
						{
							return;
						}
					}
				}
			}
						TabloPatch.DevAdminForm f = new TabloPatch.DevAdminForm(cfg, this._settingForm.Size);
			f.ShowDialog();
		}
			 		private void Patch_ReconnectPult()
 		{
 			try
 			{
 				if (this._Controlboard != null)
 				{
 					this._Controlboard.Dispose();
 					this._Controlboard = null;
 				}
 				this.InitialControlBoard();
 				TabloPatch.SerialPortLog.Write("ПУЛЬТ: ручное переподключение порта, статус=" + (this._Controlboard != null ? "открыт" : "не открыт"));
 			}
 			catch (Exception ex)
 			{
 				TabloPatch.SerialPortLog.Write("ПУЛЬТ: ошибка переподключения: " + ex.Message);
 			}
 		}

					private void Patch_WebTimeCmd(string key, int p)
		{
			if (key == "cfg_main")
			{
				if (p <= 0)
				{
					TabloPatch.SerialPortLog.Write("WEB: отклонено ВРЕМЯ ПЕРИОДА: значение " + p);
					return;
				}
				this._settingForm.cfg.GameTime = TimeSpan.FromSeconds((double)p);
				this._settingForm.cfg.Save();
				this.Patch_SyncGuardFromSettings();
			}
			else if (key == "cfg_ot")
			{
				if (p <= 0)
				{
					return;
				}
				this.Patch_WriteOvertimeFile(p);
				this.Patch_SyncGuardFromSettings();
			}
			else if (key == "break_set")
			{
				if (p <= 0)
				{
					return;
				}
				this._settingForm.cfg.BreakTime = TimeSpan.FromSeconds((double)p);
				this._settingForm.cfg.Save();
			}
			else if (key == "speed_set")
			{
				ComboBox cb = this.Patch_GetCbSpeed();   // БЛОК 4, F9-05: из кэша
				if (cb != null)
				{
					string want = (p / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
					if (cb.Items.Contains(want))
					{
						cb.SelectedItem = want;
						TabloPatch.SerialPortLog.Write("WEB: скорость хода часов установлена " + want);
					}
					else
					{
						TabloPatch.SerialPortLog.Write("WEB: скорость " + want + " не входит в список доступных — не изменена");
					}
				}
			}
			else if (key == "ref_remember")
			{
				this.Patch_SyncGuardFromSettings();
				TabloPatch.PatchEntry.Core.RememberPeriodTimesFromSettings();
			}
			else if (key == "match_start_set")
			{
				if (p < 0 || p > 1439)
				{
					return;
				}
				this._settingForm.cfg.MatchStart = (p / 60).ToString("D2") + ":" + (p % 60).ToString("D2");
				this._settingForm.cfg.Save();
				TabloPatch.SerialPortLog.Write("WEB: время начала матча сохранено " + this._settingForm.cfg.MatchStart);
			}
			else if (key == "prestart_arm")
			{
				if (p < 0 || p > 1439)
				{
					return;
				}
				this._settingForm.cfg.MatchStart = (p / 60).ToString("D2") + ":" + (p % 60).ToString("D2");
				this._settingForm.cfg.Save();
				DateTime nowDt = DateTime.Now;
				DateTime startDt = new DateTime(nowDt.Year, nowDt.Month, nowDt.Day, p / 60, p % 60, 0);
				TimeSpan diffTs = startDt - nowDt;
				if (diffTs < TimeSpan.Zero)
				{
					diffTs = diffTs + TimeSpan.FromDays(1.0);
				}
				TimeSpan cutTs = TimeSpan.FromSeconds((double)(int)Math.Ceiling(diffTs.TotalSeconds));
				this._settingForm.cfg.TimeBeforeStart = cutTs;
				this._settingForm.cfg.Save();
				if (TabloPatch.ExtOut.BeforeTotalSetter != null)
				{
					TabloPatch.ExtOut.BeforeTotalSetter(cutTs);
				}
				TabloPatch.SerialPortLog.Write("WEB: обратный таймер взведён на " + (int)cutTs.TotalSeconds + " сек (старт " + this._settingForm.cfg.MatchStart + ")");
			}
				 	else if (key == "to_h_dec")
 	{
 		if (this.TimeOutCounter_L > 0)
 		{
 			this.TimeOutCounter_L -= 1;
 			// БЛОК 4, F6-05: изменение счётчика тайм-аутов видно в логе
 			TabloPatch.SerialPortLog.Write("СЧЁТЧИКИ ТАЙМ-АУТОВ (веб): дома уменьшен до " + this.TimeOutCounter_L);
 		}
 		if (this._GameMode == frmMain.gameMode.Timeout && this._patchTimeoutOwner != 2)
 		{
 			this.Cmd_Stop();
 		}
 	}
 	else if (key == "to_a_dec")
 	{
 		if (this.TimeOutCounter_R > 0)
 		{
 			this.TimeOutCounter_R -= 1;
 			// БЛОК 4, F6-05: изменение счётчика тайм-аутов видно в логе
 			TabloPatch.SerialPortLog.Write("СЧЁТЧИКИ ТАЙМ-АУТОВ (веб): гости уменьшен до " + this.TimeOutCounter_R);
 		}
 		if (this._GameMode == frmMain.gameMode.Timeout && this._patchTimeoutOwner != 1)
 		{
 			this.Cmd_Stop();
 		}
 	}
			else if (key == "god_toggle")
			{
				TabloPatch.WebServer.GodMode = !TabloPatch.WebServer.GodMode;
				TabloPatch.SerialPortLog.Write("WEB: режим бога " + (TabloPatch.WebServer.GodMode ? "ВКЛЮЧЕН" : "ВЫКЛЮЧЕН"));
			}
		}
		public void Patch_SyncGuardFromSettings()
		{
			if (this._settingForm == null)
			{
				return;
			}
			TabloPatch.GameClockCore core = TabloPatch.PatchEntry.Core;
			core.PeriodTimeSec = (int)this._settingForm.cfg.GameTime.TotalSeconds;
			core.OvertimeTimeSec = this.Patch_OvertimeSecFromFiles();
		}

		private void Patch_PeriodEndRestore()
		{
			if (this._settingForm == null)
			{
				return;
			}
			TabloPatch.GameClockCore core = TabloPatch.PatchEntry.Core;
			this.Patch_SyncGuardFromSettings();
			core.RememberPeriodTimesFromSettings();
			core.SetCurrentPeriod(this.Patch_CurrentPeriodNumber());
			core.OnPeriodEnd();
			int mainSec = core.PeriodTimeSec;
			// БЛОК 2, F6-03: каждая сверка с эталоном видна в логе (что и для какого периода)
			TabloPatch.SerialPortLog.Write("ЭТАЛОН: сверка в конце периода " + this.Patch_CurrentPeriodNumber().ToString() + " — GameTime=" + mainSec.ToString() + "с, ОТ=" + core.OvertimeTimeSec.ToString() + "с");
			if ((int)this._settingForm.cfg.GameTime.TotalSeconds != mainSec)
			{
				this._settingForm.cfg.GameTime = TimeSpan.FromSeconds((double)mainSec);
				this._settingForm.cfg.Save();
			}
			int otSec = core.OvertimeTimeSec;
			if (this.Patch_OvertimeSecFromFiles() != otSec)
			{
				this.Patch_WriteOvertimeFile(otSec);
			}
			this.gB_Tablo.Tag = null;
			
		}
		// ================== БЛОК 2: НОВЫЕ МЕТОДЫ (F2-01, F6-04, F8-01) ==================

		// F2-01. Фиксация базы отсчёта времени матча (_patchStartTicks + _patchBaseSeconds).
		// Вызывается автоматически из сеттера _gameTime и явно перед стартом таймера.
		private void Patch_GameTimeMarkStart()
		{
			this._patchStartTicks = Environment.TickCount;
			this._patchBaseSeconds = this._patchGameTimeVal.TotalSeconds;
			this._patchBaseSet = true;
		}

		// F2-01. Пересчёт времени матча по РЕАЛЬНОМУ времени (Environment.TickCount).
		// Было: _gameTime += Period*num каждый тик — любой фриз UI "съедал" секунды матча
		// (до 10-40 с за игру). Стало: задержки UI на ход часов не влияют.
		// Разность int корректна даже при переполнении TickCount (~49.7 суток).
		// Пишем напрямую в поле (мимо свойства), чтобы не сбрасывать базу каждый тик.
		private void Patch_GameTimeRefresh(double speed)
		{
			if (!this._patchBaseSet)
			{
				return;
			}
			if (speed <= 0.0)
			{
				speed = 1.0;
			}
			int elapsedMs = Environment.TickCount - this._patchStartTicks;
			if (elapsedMs < 0)
			{
				elapsedMs = 0;
			}
			this._patchGameTimeVal = TimeSpan.FromSeconds(this._patchBaseSeconds + (double)elapsedMs / 1000.0 * speed);
		}

		// F6-04. Автосохранение состояния матча в match_state.xml (рядом с программой,
		// Q18: папка переносится целиком). Пишем не чаще раза в 2 с и только при изменениях.
		private System.Windows.Forms.Timer _patchStateTimer;

		private string _patchLastStateXml;

		private int _patchLastStateSaveTick;

		private string Patch_StateFilePath()
		{
			return Path.Combine(Application.StartupPath, "match_state.xml");
		}

		private void Patch_StartStateAutosave()
		{
			if (this._patchStateTimer != null)
			{
				return;
			}
			this._patchStateTimer = new System.Windows.Forms.Timer();
			this._patchStateTimer.Interval = 2000;
			this._patchStateTimer.Tick += delegate(object s, EventArgs ev)
			{
				this.Patch_AutosaveState(false);
			};
			this._patchStateTimer.Start();
		}

		private void Patch_AutosaveState(bool force)
		{
			try
			{
				string xml = this.Patch_BuildStateXml();
				if (xml == this._patchLastStateXml)
				{
					return;
				}
				int now = Environment.TickCount;
				if (!force && now - this._patchLastStateSaveTick < 2000)
				{
					return;
				}
				Patch_WriteFileAtomic(this.Patch_StateFilePath(), xml);
				this._patchLastStateSaveTick = now;
				this._patchLastStateXml = xml;
			}
			catch (Exception ex)
			{
				TabloPatch.SerialPortLog.Write("АВТОСОХРАНЕНИЕ: не удалось записать match_state.xml: " + ex.Message);
			}
		}

		private void Patch_ClearSavedState()
		{
			try
			{
				string path = this.Patch_StateFilePath();
				if (File.Exists(path))
				{
					File.Delete(path);
				}
				string bak = path + ".bak";
				if (File.Exists(bak))
				{
					File.Delete(bak);
				}
			}
			catch
			{
			}
			this._patchLastStateXml = null;
		}

		private string Patch_BuildStateXml()
		{
			StringBuilder sb = new StringBuilder();
			sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<match_state ver=\"1\" saved=\"").Append(DateTime.Now.ToString("s")).Append("\">\r\n");
			sb.Append("<period>").Append((int)this.nUD_Period.Value).Append("</period>\r\n");
			sb.Append("<score_h>").Append((int)this.nUD_Score_1.Value).Append("</score_h>\r\n");
			sb.Append("<score_a>").Append((int)this.nUD_Score_2.Value).Append("</score_a>\r\n");
			sb.Append("<time_sec>").Append((int)Math.Round(this._gameTime.TotalSeconds)).Append("</time_sec>\r\n");
			sb.Append("<total_sec>").Append((int)Math.Round(this._gameTotalTime.TotalSeconds)).Append("</total_sec>\r\n");
			sb.Append("<to_h>").Append(this.TimeOutCounter_L).Append("</to_h>\r\n");
			sb.Append("<to_a>").Append(this.TimeOutCounter_R).Append("</to_a>\r\n");
			sb.Append("<mode>").Append((int)this._GameMode).Append("</mode>\r\n");
			sb.Append("<over>").Append(this._gameOver ? 1 : 0).Append("</over>\r\n");
			sb.Append("<stroke>").Append(this._gameStroke ? 1 : 0).Append("</stroke>\r\n");
			sb.Append("<pen_a>").Append(System.Security.SecurityElement.Escape(this.Patch_JoinPenalties(this.lB_DelPeople_A))).Append("</pen_a>\r\n");
			sb.Append("<pen_b>").Append(System.Security.SecurityElement.Escape(this.Patch_JoinPenalties(this.lB_DelPeople_B))).Append("</pen_b>\r\n");
			// БЛОК 4, F6-08: архив времени сохраняется вместе с состоянием матча
			// (строки разделяются переводом строки — внутри них уже есть «;»).
			try
			{
				ListBox lbArchSave = this.Patch_GetArchiveList();
				if (lbArchSave != null && lbArchSave.Items.Count > 0)
				{
					System.Text.StringBuilder archSb = new System.Text.StringBuilder();
					foreach (object oArch in lbArchSave.Items)
					{
						string tArch = oArch as string;
						if (tArch == null)
						{
							continue;
						}
						if (archSb.Length > 0)
						{
							archSb.Append("\n");
						}
						archSb.Append(tArch);
					}
					sb.Append("<archive>").Append(System.Security.SecurityElement.Escape(archSb.ToString())).Append("</archive>\r\n");
				}
			}
			catch (Exception exArch)
			{
				TabloPatch.Diag.Note("Сохранение архива в match_state", exArch);
			}
			sb.Append("</match_state>\r\n");
			return sb.ToString();
		}

		private string Patch_JoinPenalties(ListBox lb)
		{
			// БЛОК 4, F9-04: StringBuilder вместо конкатенации строк
			if (lb == null)
			{
				return "";
			}
			System.Text.StringBuilder sbJoin = new System.Text.StringBuilder();
			foreach (object o in lb.Items)
			{
				string tItem = o as string;
				if (tItem == null)
				{
					continue;
				}
				if (sbJoin.Length > 0)
				{
					sbJoin.Append(";");
				}
				sbJoin.Append(tItem);
			}
			return sbJoin.ToString();
		}

		// Атомарная запись небольшого текстового файла: tmp (WriteThrough + Flush НА ДИСК)
		// -> File.Replace с копией в .bak (на FAT32 — откат к Delete+Move). Обрыв питания
		// в любой момент оставляет на диске ЦЕЛЫЙ файл (старый или новый).
		// БЛОК 2: в .NET 3.5 у FileStream НЕТ Flush(bool) (появился в .NET 4.0),
		// поэтому настоящий сброс на диск делаем Win32-вызовом FlushFileBuffers.
		[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool FlushFileBuffers(System.Runtime.InteropServices.SafeHandle hFile);

		private static void Patch_FlushToDisk(FileStream fs)
		{
			try
			{
				FlushFileBuffers(fs.SafeFileHandle);
			}
			catch
			{
			}
		}

		private static void Patch_WriteFileAtomic(string path, string content)
		{
			string tmp = path + ".tmp";
			byte[] data = new UTF8Encoding(false).GetBytes(content);
			using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
			{
				fs.Write(data, 0, data.Length);
				fs.Flush();
				Patch_FlushToDisk(fs);   // .NET 3.5: Flush(true) нет — сбрасываем на диск через Win32
			}
			if (File.Exists(path))
			{
				bool replaced = false;
				try
				{
					File.Replace(tmp, path, path + ".bak", true);
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

		// F6-04. При старте: если найден свежий (< 6 ч) и "не чистый" match_state.xml —
		// предлагаем восстановить матч (обрыв питания больше не теряет счёт/период/время).
		private void Patch_OfferRestoreState()
		{
			try
			{
				string path = this.Patch_StateFilePath();
				if (!File.Exists(path))
				{
					return;
				}
				System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
				doc.Load(path);
				System.Xml.XmlNode root = doc.DocumentElement;
				if (root == null || root.Name != "match_state")
				{
					return;
				}
				string savedStr = (root.Attributes != null && root.Attributes["saved"] != null) ? root.Attributes["saved"].Value : "";
				DateTime savedAt;
				if (!DateTime.TryParse(savedStr, out savedAt))
				{
					savedAt = DateTime.Now;
				}
				double ageMin = (DateTime.Now - savedAt).TotalMinutes;
				if (ageMin > 360.0 || ageMin < -5.0)
				{
					return;
				}
				int period = Patch_XmlInt(root, "period", 1);
				int scoreH = Patch_XmlInt(root, "score_h", 0);
				int scoreA = Patch_XmlInt(root, "score_a", 0);
				int timeSec = Patch_XmlInt(root, "time_sec", 0);
				int totalSec = Patch_XmlInt(root, "total_sec", 0);
				string penA = Patch_XmlStr(root, "pen_a");
				string penB = Patch_XmlStr(root, "pen_b");
				bool clean = period <= 1 && scoreH == 0 && scoreA == 0 && penA.Length == 0 && penB.Length == 0 && (timeSec == 0 || (totalSec > 0 && timeSec == totalSec));
				if (clean)
				{
					this.Patch_ClearSavedState();
					return;
				}
				// БЛОК 4, F6-08: в диалоге честно показывается и восстанавливаемый архив
				int archCount = Patch_XmlStr(root, "archive").Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
				DialogResult r = MessageBox.Show("Найдено состояние матча от " + savedAt.ToString("dd.MM.yyyy HH:mm:ss") + " (" + ((int)ageMin).ToString() + " мин назад).\r\n\r\nПериод " + period.ToString() + ", счёт " + scoreH.ToString() + ":" + scoreA.ToString() + ", время " + FmtClock(TimeSpan.FromSeconds((double)timeSec), false) + ".\r\nУдаления: " + Patch_CountPen(penA).ToString() + " у А, " + Patch_CountPen(penB).ToString() + " у Б.\r\nАрхив времени: " + archCount.ToString() + " записей.\r\n\r\nВосстановить матч? (часы встанут на паузу — запуск вручную)", "Табло — восстановление после сбоя", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
				if (r == DialogResult.Yes)
				{
					this.Patch_ApplyState(root);
				}
				else
				{
					this.Patch_ClearSavedState();
				}
				this._patchLastStateXml = null;
			}
			catch (Exception ex)
			{
				TabloPatch.SerialPortLog.Write("ВОССТАНОВЛЕНИЕ: match_state.xml не прочитан, старт как обычно: " + ex.Message);
			}
		}

		private void Patch_ApplyState(System.Xml.XmlNode root)
		{
			try
			{
				this.Cmd_Stop();
				int period = Patch_XmlInt(root, "period", 1);
				if (period > (int)this.nUD_Period.Maximum)
				{
					this.nUD_Period.Maximum = (decimal)period;
				}
				this.nUD_Period.Value = (decimal)period;
				this.nUD_Score_1.Value = (decimal)Patch_XmlInt(root, "score_h", 0);
				this.nUD_Score_2.Value = (decimal)Patch_XmlInt(root, "score_a", 0);
				this.TimeOutCounter_L = Patch_XmlInt(root, "to_h", 0);
				this.TimeOutCounter_R = Patch_XmlInt(root, "to_a", 0);
				this._gameOver = Patch_XmlInt(root, "over", 0) != 0;
				this._gameStroke = Patch_XmlInt(root, "stroke", 1) != 0;
				int totalSec = Patch_XmlInt(root, "total_sec", 0);
				if (totalSec >= 1)
				{
					this._gameTotalTime = TimeSpan.FromSeconds((double)totalSec);
				}
				int timeSec = Patch_XmlInt(root, "time_sec", 0);
				if (timeSec < 0)
				{
					timeSec = 0;
				}
				if (this._gameTotalTime.TotalSeconds >= 1.0 && (double)timeSec > this._gameTotalTime.TotalSeconds)
				{
					timeSec = (int)this._gameTotalTime.TotalSeconds;
				}
				this._gameTime = TimeSpan.FromSeconds((double)timeSec);   // свойство само зафиксирует базу (F2-01)
				this.lB_DelPeople_A.Items.Clear();
				this.lB_DelPeople_B.Items.Clear();
				string[] pa = Patch_SplitPen(Patch_XmlStr(root, "pen_a"));
				string[] pb = Patch_SplitPen(Patch_XmlStr(root, "pen_b"));
				for (int i = 0; i < pa.Length; i++)
				{
					this.lB_DelPeople_A.Items.Add(pa[i]);
				}
				for (int j = 0; j < pb.Length; j++)
				{
					this.lB_DelPeople_B.Items.Add(pb[j]);
				}
				// БЛОК 4, F6-08: восстанавливаем и архив времени (если он был сохранён)
				try
				{
					ListBox lbArchLoad = this.Patch_GetArchiveList();
					if (lbArchLoad != null)
					{
						lbArchLoad.Items.Clear();
						string archRaw = Patch_XmlStr(root, "archive");
						int maxNo = 0;
						if (archRaw.Length > 0)
						{
							foreach (string archLine in archRaw.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
							{
								lbArchLoad.Items.Add(archLine);
								int spIdx = archLine.IndexOf(' ');
								int archNo;
								if (archLine.Length > 1 && archLine[0] == '\u2116' && spIdx > 1 && int.TryParse(archLine.Substring(1, spIdx - 1), out archNo) && archNo > maxNo)
								{
									maxNo = archNo;
								}
							}
						}
						lbArchLoad.Tag = maxNo;
						// ПУНКТ 12: после восстановления архив тоже ограничен 6 записями —
						// в старом match_state.xml их могло быть до 40. Оставляем самые свежие
						// (счётчик номеров maxNo не меняется, нумерация стопов продолжается).
						while (lbArchLoad.Items.Count > 6)
						{
							lbArchLoad.Items.RemoveAt(0);
						}
					}
				}
				catch (Exception exArchL)
				{
					TabloPatch.Diag.Note("Восстановление архива из match_state", exArchL);
				}
				this.ShowPenalty(0, this.lB_DelPeople_A, this.gs_LWindow_2);
				this.ShowPenalty(1, this.lB_DelPeople_A, this.gs_LWindow_3);
				this.ShowPenalty(0, this.lB_DelPeople_B, this.gs_RWindow_2);
				this.ShowPenalty(1, this.lB_DelPeople_B, this.gs_RWindow_3);
				this.TabloMode();
				this.statusShow();
				TabloPatch.SerialPortLog.Write("ВОССТАНОВЛЕНИЕ: матч восстановлен из match_state.xml — период " + period.ToString() + ", счёт " + ((int)this.nUD_Score_1.Value).ToString() + ":" + ((int)this.nUD_Score_2.Value).ToString() + ", время " + timeSec.ToString() + " с, удаления A=" + pa.Length.ToString() + " B=" + pb.Length.ToString() + ". Часы на паузе.");
			}
			catch (Exception ex)
			{
				TabloPatch.SerialPortLog.Write("ВОССТАНОВЛЕНИЕ: сбой применения состояния: " + ex.Message);
			}
		}

		private static int Patch_XmlInt(System.Xml.XmlNode root, string name, int def)
		{
			try
			{
				System.Xml.XmlNode n = root.SelectSingleNode(name);
				if (n == null)
				{
					return def;
				}
				int v;
				if (int.TryParse(n.InnerText.Trim(), out v))
				{
					return v;
				}
			}
			catch
			{
			}
			return def;
		}

		private static string Patch_XmlStr(System.Xml.XmlNode root, string name)
		{
			try
			{
				System.Xml.XmlNode n = root.SelectSingleNode(name);
				if (n != null)
				{
					return n.InnerText;
				}
			}
			catch
			{
			}
			return "";
		}

		private static int Patch_CountPen(string s)
		{
			if (s == null || s.Trim().Length == 0)
			{
				return 0;
			}
			return Patch_SplitPen(s).Length;
		}

		private static string[] Patch_SplitPen(string s)
		{
			if (s == null || s.Trim().Length == 0)
			{
				return new string[0];
			}
			string[] parts = s.Split(new char[] { ';' });
			List<string> res = new List<string>();
			for (int i = 0; i < parts.Length; i++)
			{
				if (parts[i].Trim().Length > 0)
				{
					res.Add(parts[i].Trim());
				}
			}
			return res.ToArray();
		}

		// F8-01. Остановка и освобождение winmm-таймера при закрытии формы.
		private static void Patch_KillTimer(ref Multimedia.Timer t)
		{
			if (t == null)
			{
				return;
			}
			try
			{
				if (t.IsRunning)
				{
					t.Stop();
				}
			}
			catch
			{
			}
			try
			{
				t.Dispose();
			}
			catch
			{
			}
			t = null;
		}

		// ================== КОНЕЦ БЛОКА 2 ==================

		private void Patch_RemovePenalized(int playerNumber)
		{
			// БЛОК 4, F6-10: валидация номера + ЧЕСТНЫЙ лог («не найден» больше не выглядит
			// как успешное снятие).
			if (playerNumber < 1 || playerNumber > 99)
			{
				TabloPatch.SerialPortLog.Write("УДАЛЕНИЕ СНЯТО: отклонено — номер " + playerNumber + " вне диапазона 1..99");
				return;
			}
			string prefix = playerNumber.ToString("D2");
			int removed = 0;
			for (int i = this.lB_DelPeople_A.Items.Count - 1; i >= 0; i--)
			{
				string text = this.lB_DelPeople_A.Items[i] as string;
				if (text != null && text.Length >= 2 && text.Substring(0, 2) == prefix)
				{
					this.lB_DelPeople_A.Items.RemoveAt(i);
					removed++;
				}
			}
			for (int j = this.lB_DelPeople_B.Items.Count - 1; j >= 0; j--)
			{
				string text2 = this.lB_DelPeople_B.Items[j] as string;
				if (text2 != null && text2.Length >= 2 && text2.Substring(0, 2) == prefix)
				{
					this.lB_DelPeople_B.Items.RemoveAt(j);
					removed++;
				}
			}
			this.ShowPenalty(0, this.lB_DelPeople_A, this.gs_LWindow_2);
			this.ShowPenalty(1, this.lB_DelPeople_A, this.gs_LWindow_3);
			this.ShowPenalty(0, this.lB_DelPeople_B, this.gs_RWindow_2);
			this.ShowPenalty(1, this.lB_DelPeople_B, this.gs_RWindow_3);
			this.TabloMode();
			this.statusShow();
			if (removed == 0)
			{
				TabloPatch.SerialPortLog.Write("УДАЛЕНИЕ СНЯТО: игрок №" + playerNumber + " НЕ НАЙДЕН в списках удалений — снимать нечего");
			}
			else
			{
				TabloPatch.SerialPortLog.Write("УДАЛЕНИЕ СНЯТО: игрок №" + playerNumber + " убран с табло немедленно, записей снято: " + removed + " (период " + (int)this.nUD_Period.Value + ")");
			}
		}

		// ================== ПУНКТ 4: СНЯТИЕ СТРОКИ ИЗ АКТИВНОГО СПИСКА (ВЕБ) ==================
		// Снимается ТОЛЬКО выделенная строка и ТОЛЬКО у своей команды. Список второй
		// команды не просматривается вообще — одинаковые номера у хозяев и гостей
		// больше не приводят к снятию обеих строк.
		// Физический пульт сюда не попадает: он идёт через _Controlboard_OnCommandDelGamer
		// и Patch_RemoveFromList() (протокол и команды пульта не изменены).
		private void Patch_WebRemovePenalty(string key, int p)
		{
			try
			{
				bool home = (key == "pen_rem_h" || key == "pen_rem_row_h");
				bool byRow = (key == "pen_rem_row_h" || key == "pen_rem_row_a");
				ListBox lb = home ? this.lB_DelPeople_A : this.lB_DelPeople_B;
				string teamName = home ? "хозяева" : "гости";
				if (lb == null)
				{
					return;
				}
				int number;
				int index;
				if (byRow)
				{
					int slot = p / 100;
					number = p % 100;
					if (slot < 0 || slot > 1 || number < 1 || number > 99)
					{
						TabloPatch.SerialPortLog.Write("СНЯТИЕ (веб): отклонено — недопустимые параметры строки («" + p + "») для команды «" + teamName + "»");
						return;
					}
					// Строка могла измениться между опросами (штраф истёк, список сдвинулся) —
					// снимаем только если в этой клетке до сих пор тот же игрок. Иначе НИЧЕГО
					// не снимаем: лучше честный отказ, чем снятие не той строки.
					if (slot >= lb.Items.Count || !this.Patch_RowIsPlayer(lb.Items[slot] as string, number))
					{
						TabloPatch.SerialPortLog.Write("СНЯТИЕ (веб): отклонено — строка " + (slot + 1).ToString() + " команды «" + teamName + "» больше не содержит игрока №" + number.ToString() + " (список изменился). Ничего не снято.");
						return;
					}
					index = slot;
				}
				else
				{
					number = p;
					if (number < 1 || number > 99)
					{
						TabloPatch.SerialPortLog.Write("СНЯТИЕ (веб): отклонено — номер " + number.ToString() + " вне диапазона 1..99 (команда «" + teamName + "»)");
						return;
					}
					index = this.Patch_FindRowIndex(lb, number);
					if (index < 0)
					{
						TabloPatch.SerialPortLog.Write("СНЯТИЕ (веб): игрок №" + number.ToString() + " у команды «" + teamName + "» НЕ НАЙДЕН — снимать нечего (вторая команда не проверялась)");
						return;
					}
				}
				string rowText = lb.Items[index] as string;
				lb.Items.RemoveAt(index);
				TabloPatch.SerialPortLog.Write("СНЯТИЕ (веб): команда «" + teamName + "», строка " + (index + 1).ToString() + " [" + (rowText ?? "") + "] снята. Список второй команды НЕ затронут.");
				this.Patch_AfterPenaltyRowRemoved(home);
			}
			catch (Exception exRem)
			{
				TabloPatch.Diag.Note("Снятие строки удаления из веба", exRem);
			}
		}

		// ПУНКТ 4: строка списка принадлежит игроку с таким номером?
		// Формат строки: "NN MM:SS" (первые два символа — номер игрока).
		private bool Patch_RowIsPlayer(string row, int number)
		{
			if (row == null || row.Length < 2)
			{
				return false;
			}
			return row.Substring(0, 2) == number.ToString("D2");
		}

		// ПУНКТ 4: индекс ПЕРВОЙ строки этого игрока в списке ОДНОЙ команды.
		private int Patch_FindRowIndex(ListBox lb, int number)
		{
			if (lb == null)
			{
				return -1;
			}
			for (int i = 0; i < lb.Items.Count; i++)
			{
				if (this.Patch_RowIsPlayer(lb.Items[i] as string, number))
				{
					return i;
				}
			}
			return -1;
		}

		// ПУНКТ 4: после снятия строки перерисовываем только СВОЮ половину табло.
		// Схема клеток 2×2 (как на табло и в окне снятия):
		//	1×1 - хозяева (gs_LWindow_2), 1×2 - гости (gs_RWindow_2),
		//	2×1 - хозяева (gs_LWindow_3), 2×2 - гости (gs_RWindow_3).
		// Гости начинаются с правой половины — вторая команда всегда в правых клетках.
		private void Patch_AfterPenaltyRowRemoved(bool home)
		{
			if (home)
			{
				this.ShowPenalty(0, this.lB_DelPeople_A, this.gs_LWindow_2);
				this.ShowPenalty(1, this.lB_DelPeople_A, this.gs_LWindow_3);
			}
			else
			{
				this.ShowPenalty(0, this.lB_DelPeople_B, this.gs_RWindow_2);
				this.ShowPenalty(1, this.lB_DelPeople_B, this.gs_RWindow_3);
			}
			this.TabloMode();
			this.statusShow();
		}

		private void Patch_RemoteSetTimer(int enteredSec, bool forwardStroke, bool autoWhistle)
		{
			if (this._settingForm == null || enteredSec < 0)
			{
				// БЛОК 4, F5-07: отказ больше не молчаливый
				TabloPatch.SerialPortLog.Write("ВРЕМЯ (пульт/веб): установка отклонена — значение " + enteredSec + " (допустимо 0 и выше) или окно настроек недоступно");
				return;
			}
			// БЛОК 4, F5-07: фиксируем ВСЕ побочные эффекты операции одной строкой
			TabloPatch.SerialPortLog.Write("ВРЕМЯ (пульт/веб): установка длины периода " + enteredSec + " с; ход=" + (forwardStroke ? "прямой" : "обратный") + "; авто-свисток=" + (autoWhistle ? "вкл" : "выкл") + "; старое GameTime=" + (int)this._settingForm.cfg.GameTime.TotalSeconds + " с; период=" + (int)this.nUD_Period.Value);
			this._settingForm.cfg.ForwardStrokeGame = forwardStroke;
			this._settingForm.cfg.AutoWhistleEnable = autoWhistle;
			TabloPatch.GameClockCore core = TabloPatch.PatchEntry.Core;
			this.Patch_SyncGuardFromSettings();
			core.RememberPeriodTimesFromSettings();
			core.SetCurrentPeriod((int)this.nUD_Period.Value);
			core.ApplyManualTimeCorrection(enteredSec);
			this._settingForm.cfg.GameTime = TimeSpan.FromSeconds((double)enteredSec);
			this._settingForm.cfg.Save();
			if ((int)this.nUD_Period.Value >= 4)
			{
				this.gB_Tablo.Tag = "otmanual";
				this.Patch_WriteOvertimeFile(enteredSec);
			}
			this._gameStroke = this._settingForm.cfg.ForwardStrokeGame;
			if (!this._gameStroke)
			{
				this._gameTotalTime = TimeSpan.FromSeconds((double)enteredSec);
				if (this._gameTotalTime.TotalSeconds < 1.0)
				{
					// БЛОК 4, F6-15: подстановка не молчаливая
					TabloPatch.SerialPortLog.Write("ВРЕМЯ: введено меньше 1 с — подставлена длина периода 1:00");
					this._gameTotalTime = TimeSpan.FromMinutes(1.0);
				}
				this._gameTime = TimeSpan.Zero;
			}
			else
			{
				this._gameTime = TimeSpan.FromSeconds((double)enteredSec);
				if (this._gameTotalTime <= this._gameTime)
				{
					this._gameTotalTime = this._gameTime + TimeSpan.FromSeconds(60.0);
				}
			}
			this.TabloMode();
		}

		private void Patch_ResetToSaved()
		{
			TabloPatch.GameClockCore core = TabloPatch.PatchEntry.Core;
			this.Patch_SyncGuardFromSettings();
			core.SetCurrentPeriod((int)this.nUD_Period.Value);
			int saved = core.DomainSavedSec;
			if (saved <= 0)
			{
				saved = core.DomainSettingSec;
			}
			if (!this._gameStroke)
			{
				this._gameTotalTime = TimeSpan.FromSeconds((double)saved);
				this._gameTime = TimeSpan.Zero;
			}
			else
			{
				this._gameTime = TimeSpan.FromSeconds((double)saved);
			}
			this.TabloMode();
			TabloPatch.SerialPortLog.Write("СБРОС ВРЕМЕНИ: к эталону " + saved + " сек");
		}

				private void Patch_ChangePeriod(int delta)
		{
			int p = (int)this.nUD_Period.Value + delta;
			if (p < 1)
			{
				p = 1;
			}
			if (p > 9)
			{
				p = 9;
			}
			// БЛОК 4, F6-07: переключение периода ВО ВРЕМЯ ИДУЩЕГО периода требует
			// подтверждения оператора (диалог появляется на экране табло; команда с
			// пульта/веба приходит через BeginInvoke в UI-поток). Режим бога (Q16) — без диалога.
			if (p != (int)this.nUD_Period.Value && this._GameMode == frmMain.gameMode.GameStarted && !TabloPatch.WebServer.GodMode)
			{
				if (MessageBox.Show("Идёт время периода!\r\nПереключиться на период " + p.ToString() + "?\r\n\r\nЧасы остановятся, время периода вернётся к эталону.", "Табло — смена периода", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
				{
					TabloPatch.SerialPortLog.Write("ПЕРИОД: переключение на " + p.ToString() + " ОТМЕНЕНО оператором (шло время периода)");
					return;
				}
			}
			int old = (int)this.nUD_Period.Value;
			this.Patch_PeriodEndRestore();
			if (p > (int)this.nUD_Period.Maximum)
			{
				this.nUD_Period.Maximum = p;
			}
			this.nUD_Period.Value = p;
			// БЛОК 4, F6-07: эталонные часы узнают о новом периоде, настройки синхронизируются
			TabloPatch.PatchEntry.Core.SetCurrentPeriod(p);
			this.Patch_SyncGuardFromSettings();
			// БЛОК 4, F6-10: истёкшие удаления вычищаются сразу при смене периода
			this.Patch_PurgeExpired();
			TabloPatch.SerialPortLog.Write("ПЕРИОД: переключение " + old.ToString() + " -> " + p.ToString() + " (пульт/веб)");
		}

				private void Patch_DispatchRemoteCommand(TabloPatch.RemoteCommand c)
		{
			try
			{
				switch (c.Cmd)
				{
					case TabloPatch.RemoteCmd.Start:
						this.Cmd_Start();
						break;
					case TabloPatch.RemoteCmd.Stop:
						this.Cmd_Stop();
						break;
					case TabloPatch.RemoteCmd.PeriodNext:
						this.Patch_ChangePeriod(1);
						break;
					case TabloPatch.RemoteCmd.PeriodPrev:
						this.Patch_ChangePeriod(-1);
						break;
					case TabloPatch.RemoteCmd.TimeSetSave:
						this.Patch_RemoteSetTimer(c.TimeSec, this._settingForm.cfg.ForwardStrokeGame, this._settingForm.cfg.AutoWhistleEnable);
						break;
					case TabloPatch.RemoteCmd.TimeResetToSaved:
						this.Patch_ResetToSaved();
						break;
									case TabloPatch.RemoteCmd.PenaltyAdd:
				{
					// БЛОК 4, F6-10: номер игрока 1..99 проверяется ДО добавления
					if (c.PlayerNumber < 1 || c.PlayerNumber > 99)
					{
						TabloPatch.SerialPortLog.Write("УДАЛЕНИЕ: отклонено — номер " + c.PlayerNumber + " вне диапазона 1..99 (" + c.Describe() + ")");
						break;
					}
					ListBox lb = c.HomeTeam ? this.lB_DelPeople_A : this.lB_DelPeople_B;
					if (c.TimeSec > 0)
					{
						TimeSpan ts0 = TimeSpan.FromSeconds((double)c.TimeSec);
						this.Add_del(lb, c.PlayerNumber.ToString("D2") + " " + ts0.Minutes.ToString("D2") + ":" + ts0.Seconds.ToString("D2"));
					}
					else if (c.Minutes <= 0)
					{
						this.Add_del(lb, c.PlayerNumber.ToString("D2"));
					}
					else
					{
						TimeSpan ts = TimeSpan.FromMinutes((double)c.Minutes);
						this.Add_del(lb, c.PlayerNumber.ToString("D2") + " " + ts.Minutes.ToString("D2") + ":" + ts.Seconds.ToString("D2"));
					}
					break;
				}
					case TabloPatch.RemoteCmd.PenaltyRemovePlayer:
						this.Patch_RemovePenalized(c.PlayerNumber);
						break;
					case TabloPatch.RemoteCmd.ScoreHomePlus:
						this.nUD_Score_1.Value += 1;
						break;
					case TabloPatch.RemoteCmd.ScoreHomeMinus:
						if (this.nUD_Score_1.Value > 0)
						{
							this.nUD_Score_1.Value -= 1;
						}
						break;
					case TabloPatch.RemoteCmd.ScoreAwayPlus:
						this.nUD_Score_2.Value += 1;
						break;
					case TabloPatch.RemoteCmd.ScoreAwayMinus:
						if (this.nUD_Score_2.Value > 0)
						{
							this.nUD_Score_2.Value -= 1;
						}
						break;
					case TabloPatch.RemoteCmd.TimeoutHome:
 					if (this._GameMode == frmMain.gameMode.Timeout)
 					{
 						this.Cmd_Stop();
 					}
 					else
 					{
 						this._patchTimeoutOwner = 1;
 						this.Cmd_Timeout(this.TimeOutCounter_L + 1, this.TimeOutCounter_R, false);
 					}
 					break;
 				case TabloPatch.RemoteCmd.TimeoutAway:
 					if (this._GameMode == frmMain.gameMode.Timeout)
 					{
 						this.Cmd_Stop();
 					}
 					else
 					{
 						this._patchTimeoutOwner = 2;
 						this.Cmd_Timeout(this.TimeOutCounter_L, this.TimeOutCounter_R + 1, false);
 					}
 					break;
					case TabloPatch.RemoteCmd.TimeoutRequest:
						this.Cmd_Timeout(0, 0, true);
						break;
					case TabloPatch.RemoteCmd.Intermission:
						this.Cmd_BreakGame();
						break;
					case TabloPatch.RemoteCmd.MasterReset:
						this.Cmd_ResetGame();
						break;
					case TabloPatch.RemoteCmd.SplashShow:
						this.Cmd_ShowPrompt();
						break;
					case TabloPatch.RemoteCmd.SplashHide:
						this._TabloMode = frmMain.tabloMode.game;
						this.TabloMode();
						break;
											case TabloPatch.RemoteCmd.CounterSet:
						if (this._GameMode != frmMain.gameMode.GameStarted && this._gameTime != TimeSpan.Zero)
						{
							// БЛОК 4, F5-06: отрицательное время с пульта не принимается
							int cs = c.TimeSec;
							if (cs < 0)
							{
								cs = 0;
							}
							this._gameTime = TimeSpan.FromSeconds((double)cs);
						}
						this.TabloMode();
						break;
					case TabloPatch.RemoteCmd.ScoreSet:
						// БЛОК 4, F5-06: значения с пульта обрезаются до границ 0..99
						this.Patch_SetNumeric(this.nUD_Score_1, (decimal)c.PlayerNumber);
						this.Patch_SetNumeric(this.nUD_Score_2, (decimal)c.Minutes);
						break;
					case TabloPatch.RemoteCmd.WhistleBeep:
						this.WhistleBeep();
						break;
					case TabloPatch.RemoteCmd.WhistleStart:
						this.Cmd_Start();
						break;
					case TabloPatch.RemoteCmd.WhistleStop:
						this.Cmd_Stop();
						break;
					case TabloPatch.RemoteCmd.BreakTimeSet:
						this._settingForm.cfg.BreakTime = TimeSpan.FromSeconds((double)c.TimeSec);
						this._settingForm.cfg.Save();
						break;
					case TabloPatch.RemoteCmd.TimeoutTimeSet:
						this._settingForm.cfg.TimeoutTime = TimeSpan.FromSeconds((double)c.TimeSec);
						this._settingForm.cfg.Save();
						break;
					case TabloPatch.RemoteCmd.ScriptPlay:
						if (c.PlayerNumber <= 0)
						{
							this.Cmd_ScriptStop();
						}
						else if (c.PlayerNumber - 1 < this.cLB_ScriptFiles.Items.Count)
						{
							this.cLB_ScriptFiles.SelectedIndex = c.PlayerNumber - 1;
							this.Cmd_ScriptPlay();
						}
						break;
					case TabloPatch.RemoteCmd.ScriptStop:
						this.Cmd_ScriptStop();
						break;
					case TabloPatch.RemoteCmd.SetPeriodValue:
						// БЛОК 4, F5-06: период с пульта обрезается до 1..9 (Maximum расширяется)
						{
							int pv = c.PlayerNumber;
							if (pv < 1)
							{
								pv = 1;
							}
							if (pv > 9)
							{
								pv = 9;
							}
							if (pv > (int)this.nUD_Period.Maximum)
							{
								this.nUD_Period.Maximum = (decimal)pv;
							}
							this.nUD_Period.Value = (decimal)pv;
						}
						break;
					case TabloPatch.RemoteCmd.TimeResetOnly:
						this.Cmd_TimeReset();
						break;
					case TabloPatch.RemoteCmd.DelGamerPacket:
					{
						ListBox lb2 = c.HomeTeam ? this.lB_DelPeople_A : this.lB_DelPeople_B;
						int sec2 = c.Minutes * 60;
												if (sec2 <= 0)
						{
							if (this.Patch_PlayerInList(lb2, c.PlayerNumber))
							{
								this.Patch_RemoveFromList(lb2, c.PlayerNumber);
							}
							else
							{
								this.Add_del(lb2, c.PlayerNumber.ToString("D2"));
							}
						}
						else
						{
							TimeSpan ts2 = TimeSpan.FromSeconds((double)sec2);
							this.Add_del(lb2, c.PlayerNumber.ToString("D2") + " " + ts2.Minutes.ToString("D2") + ":" + ts2.Seconds.ToString("D2"));
						}
						break;
					}
					default:
						TabloPatch.PatchEntry.Dispatcher.LogPublic("РЕЗЕРВ: команда " + c.Describe() + " проигнорирована безопасно");
						break;
				}
			}
			catch (Exception ex)
			{
				TabloPatch.PatchEntry.Dispatcher.LogPublic("ОШИБКА обработки команды: " + ex.Message);
			}
		}
				protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
		{
			 		if (keyData == (Keys.Control | Keys.F7))
 		{
 			TabloPatch.SerialPortLog.Write("HOTKEY: нажато Ctrl+F7, переподключаю пульт");
 			this.Patch_ReconnectPult();
 			return true;
 		}
			if (keyData == (Keys.Control | Keys.F8))
			{
				TabloPatch.SerialPortLog.Write("HOTKEY: нажато Ctrl+F8, открываю окно доп. вывода");
				TabloPatch.ExtOutUi.Open();
				return true;
			}
			if (keyData == (Keys.Control | Keys.F9))
			{
				TabloPatch.SerialPortLog.Write("HOTKEY: нажато Ctrl+F9, открываю эмулятор");
				TabloPatch.PatchEntry.OpenEmulator(this);
				return true;
			}
			return base.ProcessCmdKey(ref msg, keyData);
		}

		// ================== БЛОК 6, З9: СПИСОК СЦЕНАРИЕВ ==================
		// «Сохранить порядок воспроизведения» (меню Файл) — записывает ТЕКУЩИЙ порядок
		// списка сценариев в его файл (.spf), чтобы после перезапуска он не сбрасывался.
		// Если файл списка ещё не задан — предлагаем выбрать место сохранения.
		private void Patch_SaveScriptOrder()
		{
			try
			{
				string path = null;
				if (this._settingForm != null && this._settingForm.cfg != null)
				{
					path = this._settingForm.cfg.ScriptPlaylistPath;
				}
				if (string.IsNullOrEmpty(path))
				{
					SaveFileDialog sfd = new SaveFileDialog();
					sfd.Filter = "Script Playlist Files|*.spf|All Files|*.*";
					sfd.DefaultExt = ".spf";
					if (sfd.ShowDialog() != DialogResult.OK)
					{
						return;
					}
					path = sfd.FileName;
				}
				StringBuilder sbOrd = new StringBuilder();
				foreach (object oSc in this.cLB_ScriptFiles.Items)
				{
					string sSc = oSc as string;
					if (sSc == null)
					{
						continue;
					}
					sbOrd.AppendLine(sSc);
				}
				TabloPatch.TextIO.WriteAll(path, sbOrd.ToString(), true);
				if (this._settingForm != null && this._settingForm.cfg != null)
				{
					this._settingForm.cfg.ScriptPlaylistPath = path;
					this._settingForm.cfg.Save();
				}
				TabloPatch.SerialPortLog.Write("СЦЕНАРИИ: порядок воспроизведения сохранён в " + path + " (позиций: " + this.cLB_ScriptFiles.Items.Count + ")");
				this.Patch_StatusHint("Порядок воспроизведения сохранён");
			}
			catch (Exception exOrd)
			{
				TabloPatch.Diag.Note("Сохранение порядка воспроизведения", exOrd);
				MessageBox.Show("Не удалось сохранить порядок воспроизведения: " + exOrd.Message, "Табло", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
		}

		// ================== ПУНКТ 2: «ПОКАЗАТЬ В СПИСКЕ» ==================
		// БЫЛО: в активную вкладку списка видео выводился ТЕКУЩИЙ список сценариев,
		// а сохранённый порядок воспроизведения (.spf) посмотреть было нельзя.
		// СТАЛО: показывается СОДЕРЖИМОЕ СПИСКА ВОСПРОИЗВЕДЕНИЯ — того самого файла .spf,
		// который пишет пункт «Файл -> Сохранить порядок воспроизведения» и который
		// загружается при старте. Вывод — в АКТИВНУЮ вкладку из трёх
		// (Рекламная пауза / Перерыв / Таймаут); прежние записи вкладки заменяются.
		// Если файл списка ещё не задан/не читается — показываем текущий список сценариев
		// и честно пишем об этом в статус-строку и в журнал.
		// Дополнительно: если содержимое файла ОТЛИЧАЕТСЯ от текущего списка сценариев,
		// оператор видит предупреждение (значит, порядок не сохранён).
		private void Patch_ShowScriptsInVideoList()
		{
			try
			{
				TabControl tc = this.tabControl_VideoFileList;
				if (tc == null || tc.SelectedTab == null)
				{
					this.Patch_StatusHint("Нет открытого окна списка видео");
					return;
				}
				TabPage tp = tc.SelectedTab;
				ListBox target = null;
				foreach (Control cTp in tp.Controls)
				{
					ListBox lbCand = cTp as ListBox;
					if (lbCand != null)
					{
						target = lbCand;
						break;
					}
				}
				if (target == null)
				{
					this.Patch_StatusHint("В окне «" + tp.Text + "» нет списка видео");
					return;
				}
				// --- источник: файл списка воспроизведения (.spf) ---
				string plPath = null;
				if (this._settingForm != null && this._settingForm.cfg != null)
				{
					plPath = this._settingForm.cfg.ScriptPlaylistPath;
				}
				List<string> src = new List<string>();
				string srcName;
				bool fromFile = false;
				if (!string.IsNullOrEmpty(plPath) && File.Exists(plPath))
				{
					try
					{
						string[] lines = TabloPatch.TextIO.ReadAllLines(plPath);
						for (int i = 0; i < lines.Length; i++)
						{
							string t = (lines[i] == null) ? "" : lines[i].Trim();
							if (t.Length > 0)
							{
								src.Add(t);
							}
						}
						fromFile = true;
						srcName = plPath;
					}
					catch (Exception exRead)
					{
						TabloPatch.Diag.Note("Чтение списка воспроизведения .spf", exRead);
						srcName = null;
					}
				}
				else
				{
					srcName = null;
				}
				if (!fromFile)
				{
					// файла нет — показываем то, что сейчас в списке сценариев
					src.Clear();
					foreach (object oSc in this.cLB_ScriptFiles.Items)
					{
						string sp = oSc as string;
						if (!string.IsNullOrEmpty(sp))
						{
							src.Add(sp.Trim());
						}
					}
					srcName = "текущий список сценариев (файл .spf не задан или не найден)";
				}
				// --- предупреждение: файл и текущий список расходятся ---
				string diffNote = "";
				if (fromFile)
				{
					StringBuilder memSb = new StringBuilder();
					foreach (object oMem in this.cLB_ScriptFiles.Items)
					{
						string sMem = oMem as string;
						if (sMem == null)
						{
							continue;
						}
						memSb.AppendLine(sMem.Trim());
					}
					StringBuilder fileSb = new StringBuilder();
					for (int j = 0; j < src.Count; j++)
					{
						fileSb.AppendLine(src[j]);
					}
					if (memSb.ToString() != fileSb.ToString())
					{
						diffNote = " ВНИМАНИЕ: отличается от текущего списка сценариев — порядок не сохранён.";
					}
				}
				// --- вывод в активное окно ---
				target.BeginUpdate();
				int maxNmLen = 0;
				try
				{
					target.Items.Clear();
					for (int k = 0; k < src.Count; k++)
					{
						string nm;
						try
						{
							nm = Path.GetFileName(src[k]);
						}
						catch
						{
							nm = src[k];
						}
						if (nm.Length == 0)
						{
							nm = src[k];
						}
						if (nm.Length > maxNmLen)
						{
							maxNmLen = nm.Length;
						}
						target.Items.Add(nm);
					}
					target.HorizontalScrollbar = true;   // режим Scroll — только в этом окне
					// у owner-draw списков полоса прокрутки появляется только при явно
					// заданной ширине содержимого — оцениваем её по самому длинному имени
					target.HorizontalExtent = maxNmLen * 8 + 24;
				}
				finally
				{
					target.EndUpdate();
				}
				TabloPatch.SerialPortLog.Write("СПИСОК ВОСПРОИЗВЕДЕНИЯ: показан в окне «" + tp.Text + "» (источник: " + srcName
					+ "; позиций: " + target.Items.Count.ToString() + "; прежние записи окна заменены)" + diffNote);
				this.Patch_StatusHint("Список воспроизведения показан в окне «" + tp.Text + "»: " + target.Items.Count.ToString() + " поз." + diffNote);
			}
			catch (Exception exShow)
			{
				TabloPatch.Diag.Note("Показать список воспроизведения", exShow);
			}
		}
		// Token: 0x060000CB RID: 203 RVA: 0x0000D1F0 File Offset: 0x0000B3F0
		private void statusShow()
		{
			if (this.statusStrip1.Items.Count > 2)
			{
				// БЛОК 4, F6-12/F4-12: в статусе видны порт, режим, период и время последней
				// команды пульта (F5-09 — индикатор «пульт живой»). Опечатка «Связъ» исправлена.
				string portTxt;
				if (this._StatusFlags == frmMain.statusFlags.portOpen)
				{
					string pn = "";
					try
					{
						if (this._settingForm != null && this._settingForm.cfg != null && !string.IsNullOrEmpty(this._settingForm.cfg.Port))
						{
							pn = " (" + this._settingForm.cfg.Port + ")";
						}
					}
					catch
					{
					}
					portTxt = "Порт открыт" + pn + ". Связь с пультом установлена.";
				}
				else if (this._StatusFlags == frmMain.statusFlags.portNotOpen)
				{
					portTxt = "Порт не открыт. Управление с пульта невозможно. (Ctrl+F7 — переподключить)";
				}
				else
				{
					return;
				}
				string pultTxt = "";
				if (this._patchLastPultCmdUtc != DateTime.MinValue)
				{
					pultTxt = " Пульт: команда в " + this._patchLastPultCmdUtc.ToString("HH:mm:ss");
				}
				// БЛОК 6, З8: информация о периоде убрана — вместо неё количество устройств,
				// подключённых к веб-интерфейсу. Формат: «Подключенных к серверу: 1 из 2 | 0»,
				// где первое число — обычные подключения (лимит MaxDevices), после «|» — служебные.
				int webReg = 0;
				int webSup = 0;
				try
				{
					TabloPatch.WebServer.GetSessionCounts(out webReg, out webSup);
				}
				catch
				{
				}
				this.statusStrip1.Items[2].Text = portTxt + " | " + this.Patch_ModeName() + ". Подключенных к серверу: " + webReg.ToString() + " из " + TabloPatch.WebServer.MaxDevices.ToString() + " | " + webSup.ToString() + pultTxt;
			}
		}

		// Token: 0x0400005F RID: 95
		private VideoPlayerForm vpf;

		// Token: 0x04000060 RID: 96
		private TabloHockeyForm thf;

		// Token: 0x04000061 RID: 97
		private TabloHockeyForm thfPrompt;

		// Token: 0x04000062 RID: 98
		private Form thfBackground;

		// Token: 0x04000063 RID: 99
		private SettingForm _settingForm;

		// Token: 0x04000064 RID: 100
		private Multimedia.Timer _promptTimer;

		// Token: 0x04000065 RID: 101
		private frmMain.statusFlags _StatusFlags;

		// Token: 0x04000066 RID: 102
		private frmMain.gameMode _GameMode;

		// Token: 0x04000067 RID: 103
		private frmMain.tabloMode _TabloMode;

		// Token: 0x04000068 RID: 104
		private Multimedia.Timer _timerRestore;

		// Token: 0x04000069 RID: 105
		private TimeSpan _storedTime;

		// Token: 0x0400006A RID: 106
		private bool _videoEnabled;

		// Token: 0x0400006B RID: 107
		private bool _GamePlayed;

		// Token: 0x0400006C RID: 108
		private Multimedia.Timer _gameTimer;

		// Token: 0x0400006D RID: 109
		// ===== БЛОК 2, F2-01: время матча из Environment.TickCount, а не из счёта тиков =====
		// _gameTime стало свойством: ЛЮБАЯ запись (в т.ч. "+=") автоматически фиксирует базу
		// отсчёта (Patch_GameTimeMarkStart) — рассинхрон базы и значения невозможен.
		// Тик таймера пишет напрямую в _patchGameTimeVal (Patch_GameTimeRefresh), чтобы
		// не сбрасывать базу каждые 10 мс. Обработчики ПУЛЬТА присваивают _gameTime как
		// раньше — для них ничего не изменилось (протокол и DLL не тронуты, Q5).
		private TimeSpan _patchGameTimeVal;
		private int _patchStartTicks;          // Environment.TickCount на момент фиксации базы
		private double _patchBaseSeconds;      // сколько секунд уже "набежало" к этому моменту
		private bool _patchBaseSet;            // база зафиксирована
		private double _patchSpeedLast = 1.0;  // последняя применённая скорость хода времени

		private TimeSpan _gameTime
		{
			get
			{
				return this._patchGameTimeVal;
			}
			set
			{
				this._patchGameTimeVal = value;
				this.Patch_GameTimeMarkStart();
			}
		}

		// Token: 0x0400006E RID: 110
		private bool _gameStroke;

		// Token: 0x0400006F RID: 111
		private TimeSpan _gameTotalTime;

		// Token: 0x04000070 RID: 112
		private bool _gameShowOtherString;

		// Token: 0x04000071 RID: 113
		private bool _gameOver = true;

		// Token: 0x04000072 RID: 114
		private Multimedia.Timer _timeoutTimer;

		// Token: 0x04000073 RID: 115
		private TimeSpan _timeoutTime;

		// Token: 0x04000074 RID: 116
		private bool _timeoutStroke;

		// Token: 0x04000075 RID: 117
		private TimeSpan _timeoutTotalTime;

		// Token: 0x04000076 RID: 118
		private bool _timeoutVideoEnabled;

		// Token: 0x04000077 RID: 119
		private Multimedia.Timer _breakTimer;

		// Token: 0x04000078 RID: 120
		private TimeSpan _breakTime;

		// Token: 0x04000079 RID: 121
		private bool _breakStroke;

		// Token: 0x0400007A RID: 122
		private TimeSpan _breakTotalTime;

		// Token: 0x0400007B RID: 123
		private bool _breakVideoEnabled;

		// Token: 0x0400007C RID: 124
		private TimeSpan _beforeTime;

		// Token: 0x0400007D RID: 125
		private bool _beforeTimerEnabled;

		// Token: 0x0400007E RID: 126
		private bool _beforeStroke;

		// Token: 0x0400007F RID: 127
		private TimeSpan _beforeTotalTime;

		// Token: 0x04000080 RID: 128
		private TimeSpan _beforeTime_1;

		// Token: 0x04000081 RID: 129
		private bool _beforeAudio_1;

		// Token: 0x04000082 RID: 130
		private TimeSpan _beforeTime_2;

		// Token: 0x04000083 RID: 131
		private bool _beforeAudio_2;

		// Token: 0x04000084 RID: 132
		private TimeSpan _beforeTime_3;

		// Token: 0x04000085 RID: 133
		private bool _beforeAudio_3;

		// Token: 0x04000086 RID: 134
		private TimeSpan _beforeTime_4;

		// Token: 0x04000087 RID: 135
		private bool _beforeAudio_4;

		// Token: 0x04000088 RID: 136
		private TimeSpan _beforeTime_5;

		// Token: 0x04000089 RID: 137
		private bool _beforeAudio_5;

		// Token: 0x0400008A RID: 138
		private TimeSpan _beforeTime_6;

		// Token: 0x0400008B RID: 139
		private bool _beforeAudio_6;

		// Token: 0x0400008C RID: 140
		private TimeSpan _beforeTime_7;

		// Token: 0x0400008D RID: 141
		private bool _beforeAudio_7;

		// Token: 0x0400008E RID: 142
		private bool _showPromptTime_flash1;

		// Token: 0x0400008F RID: 143
		private bool _showPromptTime_flash2;

		// Token: 0x04000090 RID: 144
		private TAudioPlayer _BeforeStartPlayer;

		// Token: 0x04000091 RID: 145
		private TAudioPlayer _WhistleBeep;

		// Token: 0x04000092 RID: 146
		private TAudioPlayer _GetGateBeep;

		// Token: 0x04000093 RID: 147
		private TAudioPlayer _GameStatrBeep;

		// Token: 0x04000094 RID: 148
		private ControlBoard _Controlboard;

		// Token: 0x04000095 RID: 149
		private System.Windows.Forms.Timer _ControlboardTimer;

		// Token: 0x04000096 RID: 150
		private int _TimeOutCounter_L;

		// Token: 0x04000097 RID: 151
		private int _TimeOutCounter_R;

		private int _patchTimeoutOwner = 0;

		// БЛОК 6, З8: последнее показанное в статус-строке число веб-подключений
		// (обычные | служебные). Обновляем строку только когда счётчики изменились.
		private int _patchWebCntReg = -1;
		private int _patchWebCntSup = -1;

		// Token: 0x04000098 RID: 152
		// БЛОК 4, F9-08: поле lockTablo удалено (блокировка не использовалась по назначению)
			private WebClient _vmixClient;

		// Token: 0x04000099 RID: 153
		private byte[] FONT_DATA_Main;

		// Token: 0x0400009A RID: 154
		private GraphString gs_Main;

		// Token: 0x0400009B RID: 155
		private GraphString gs_LWindow_1;

		// Token: 0x0400009C RID: 156
		private GraphString gs_LWindow_2;

		// Token: 0x0400009D RID: 157
		private GraphString gs_LWindow_3;

		// Token: 0x0400009E RID: 158
		private GraphString gs_CentreWindow;

		// Token: 0x0400009F RID: 159
		private GraphString gs_CentreWindow_small;

		// Token: 0x040000A0 RID: 160
		private GraphString gs_RWindow_1;

		// Token: 0x040000A1 RID: 161
		private GraphString gs_RWindow_2;

		// Token: 0x040000A2 RID: 162
		private GraphString gs_RWindow_3;

		// Token: 0x040000A3 RID: 163
		private GraphString gs_Clock;

		// Token: 0x040000A4 RID: 164
		private GraphString gs_Date;

		// Token: 0x040000A5 RID: 165
		private GraphString gs_BeforeGameTime;

		// Token: 0x040000A6 RID: 166
		private string VideoFilePath;

		// Token: 0x040000A7 RID: 167
		private bool VideoPlayed;

		// Token: 0x040000A8 RID: 168
		private double CurVideoPosition;

		// Token: 0x040000A9 RID: 169
		private ListBox _NowPlayList;

		// Token: 0x02000009 RID: 9
		private enum dayOfWeek
		{
			// Token: 0x040000AD RID: 173
			ВС,
			// Token: 0x040000AE RID: 174
			ПН,
			// Token: 0x040000AF RID: 175
			ВТ,
			// Token: 0x040000B0 RID: 176
			СР,
			// Token: 0x040000B1 RID: 177
			ЧТ,
			// Token: 0x040000B2 RID: 178
			ПТ,
			// Token: 0x040000B3 RID: 179
			СБ
		}

		// Token: 0x0200000A RID: 10
		[Flags]
		private enum statusFlags
		{
			// Token: 0x040000B5 RID: 181
			none = 0,
			// Token: 0x040000B6 RID: 182
			prompt = 1,
			// Token: 0x040000B7 RID: 183
			beforeStart = 2,
			// Token: 0x040000B8 RID: 184
			startedGame = 4,
			// Token: 0x040000B9 RID: 185
			playedGame = 8,
			// Token: 0x040000BA RID: 186
			stopGame = 16,
			// Token: 0x040000BB RID: 187
			timeoutGame = 32,
			// Token: 0x040000BC RID: 188
			breakGame = 64,
			// Token: 0x040000BD RID: 189
			scriptPlay = 128,
			// Token: 0x040000BE RID: 190
			videoPlay = 256,
			// Token: 0x040000BF RID: 191
			portOpen = 512,
			// Token: 0x040000C0 RID: 192
			portNotOpen = 1024
		}

		// Token: 0x0200000B RID: 11
		private enum gameMode
		{
			// Token: 0x040000C2 RID: 194
			None,
			// Token: 0x040000C3 RID: 195
			GameStarted,
			// Token: 0x040000C4 RID: 196
			GameStoped,
			// Token: 0x040000C5 RID: 197
			Timeout,
			// Token: 0x040000C6 RID: 198
			Break
		}

		// Token: 0x0200000C RID: 12
		private enum tabloMode
		{
			// Token: 0x040000C8 RID: 200
			prompt,
			// Token: 0x040000C9 RID: 201
			video,
			// Token: 0x040000CA RID: 202
			game
		}
	}
}
