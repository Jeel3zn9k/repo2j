using System;
using System.IO;
using System.Windows.Forms;
using DirectShowLib;

namespace Tablo
{
	// Token: 0x0200001A RID: 26
	internal class TAudioPlayer : Control
	{
		// Token: 0x14000005 RID: 5
		// (add) Token: 0x0600010F RID: 271 RVA: 0x000027D4 File Offset: 0x000009D4
		// (remove) Token: 0x06000110 RID: 272 RVA: 0x000027ED File Offset: 0x000009ED
		public event EventHandler OnAudioPlayerStop;

		// Token: 0x06000111 RID: 273 RVA: 0x000104C4 File Offset: 0x0000E6C4
		protected virtual void OnAudioPlayerStopEvent(object sender, EventArgs e)
		{
			EventHandler onAudioPlayerStop = this.OnAudioPlayerStop;
			if (onAudioPlayerStop != null)
			{
				onAudioPlayerStop(this, e);
			}
		}

		// Token: 0x06000112 RID: 274 RVA: 0x00002806 File Offset: 0x00000A06
		public bool GetAudioPrepared(string fileName)
		{
			return this._audioPrepared && this._fileName == fileName;
		}

		// Token: 0x06000113 RID: 275 RVA: 0x00002821 File Offset: 0x00000A21
		public TAudioPlayer()
		{
			this._audioPrepared = false;
		}

		// Token: 0x06000114 RID: 276 RVA: 0x000104E4 File Offset: 0x0000E6E4
		private void HandleGraphEvent()
		{
			if (this.mediaEventEx == null)
			{
				return;
			}
			EventCode eventCode;
			IntPtr lParam;
			IntPtr lParam2;
			while (this.mediaEventEx.GetEvent(out eventCode, out lParam, out lParam2, 0) == 0)
			{
				int num = this.mediaEventEx.FreeEventParams(eventCode, lParam, lParam2);
				if (eventCode == EventCode.Complete)
				{
					this.mediaControl.Stop();
					DsLong pCurrent = new DsLong(0L);
					num = this.mediaSeeking.SetPositions(pCurrent, AMSeekingSeekingFlags.AbsolutePositioning, null, AMSeekingSeekingFlags.NoPositioning);
					if (num < 0)
					{
						num = this.mediaControl.Stop();
						num = this.mediaControl.Run();
					}
					this.OnAudioPlayerStopEvent(this, new EventArgs());
					this.mediaEventEx = null;
					return;
				}
			}
		}

		// Token: 0x06000115 RID: 277 RVA: 0x0001057C File Offset: 0x0000E77C
		protected override void WndProc(ref Message m)
		{
			int msg = m.Msg;
			if (msg == 1037)
			{
				this.HandleGraphEvent();
			}
			base.WndProc(ref m);
		}

		// Token: 0x06000116 RID: 278 RVA: 0x000105A8 File Offset: 0x0000E7A8
		public int PrepareSound(string fileName)
		{
			int result = 0;
			if (File.Exists(fileName))
			{
				try
				{
					this._audioPrepared = false;
					this._fileName = fileName;
					this.CleanUp();
					if (this.graphBuilder == null)
					{
						this.graphBuilder = (IGraphBuilder)new FilterGraph();
					}
					int hr = this.graphBuilder.RenderFile(this._fileName, null);
					DsError.ThrowExceptionForHR(hr);
					this.mediaControl = (IMediaControl)this.graphBuilder;
					this.mediaEventEx = (IMediaEventEx)this.graphBuilder;
					this.mediaSeeking = (IMediaSeeking)this.graphBuilder;
					this.mediaPosition = (IMediaPosition)this.graphBuilder;
					this.basicAudio = (this.graphBuilder as IBasicAudio);
					this.basicAudio.put_Volume(-10000);
					hr = this.mediaEventEx.SetNotifyWindow(base.Handle, 1037, IntPtr.Zero);
					DsError.ThrowExceptionForHR(hr);
					this._audioPrepared = true;
					return result;
				}
				catch
				{
					this._audioPrepared = false;
					return result;
				}
			}
			result = 1;
			return result;
		}

		// Token: 0x06000117 RID: 279 RVA: 0x000106B8 File Offset: 0x0000E8B8
		public int SoundPlay()
		{
			int result = 0;
			if (this.graphBuilder != null && this.mediaControl != null)
			{
				this.mediaControl.Stop();
				this.mediaPosition.put_CurrentPosition(0.0);
				this.basicAudio.put_Volume(this.currentVolume);
				this.mediaControl.Run();
			}
			else
			{
				result = 2;
			}
			return result;
		}

		// Token: 0x06000118 RID: 280 RVA: 0x0001071C File Offset: 0x0000E91C
		public int SoundPlay(string fileName)
		{
			int result = 0;
			if (File.Exists(fileName))
			{
				this.CleanUp();
				if (this.graphBuilder == null)
				{
					this.graphBuilder = (IGraphBuilder)new FilterGraph();
				}
				int hr = this.graphBuilder.RenderFile(fileName, null);
				DsError.ThrowExceptionForHR(hr);
				this.mediaControl = (IMediaControl)this.graphBuilder;
				this.mediaEventEx = (IMediaEventEx)this.graphBuilder;
				this.mediaSeeking = (IMediaSeeking)this.graphBuilder;
				this.mediaPosition = (IMediaPosition)this.graphBuilder;
				this.basicAudio = (this.graphBuilder as IBasicAudio);
				this.basicAudio.put_Volume(-10000);
				this.mediaControl.Run();
			}
			else
			{
				result = 1;
			}
			return result;
		}

		// Token: 0x06000119 RID: 281 RVA: 0x000107E0 File Offset: 0x0000E9E0
		public void CleanUp()
		{
			if (this.mediaControl != null)
			{
				this.mediaControl.Stop();
			}
			if (this.mediaEventEx != null)
			{
				this.mediaEventEx.SetNotifyWindow(IntPtr.Zero, 0, IntPtr.Zero);
			}
			if (this.mediaControl != null)
			{
				this.mediaControl = null;
			}
			if (this.mediaPosition != null)
			{
				this.mediaPosition = null;
			}
			if (this.mediaEventEx != null)
			{
				this.mediaEventEx = null;
			}
			if (this.basicAudio != null)
			{
				this.basicAudio = null;
			}
			if (this.graphBuilder != null)
			{
				this.graphBuilder = null;
			}
			GC.Collect();
		}

		// Token: 0x040000F1 RID: 241
		public const int ER_OK = 0;

		// Token: 0x040000F2 RID: 242
		public const int ER_FileNotFound = 1;

		// Token: 0x040000F3 RID: 243
		public const int ER_AudioNotPrepared = 2;

		// Token: 0x040000F4 RID: 244
		public const int ER_AudioIsBusy = 3;

		// Token: 0x040000F5 RID: 245
		private const int WMGraphNotify = 1037;

		// Token: 0x040000F6 RID: 246
		private const int VolumeFull = 0;

		// Token: 0x040000F7 RID: 247
		private const int VolumeSilence = -10000;

		// Token: 0x040000F9 RID: 249
		private IGraphBuilder graphBuilder;

		// Token: 0x040000FA RID: 250
		private IMediaControl mediaControl;

		// Token: 0x040000FB RID: 251
		private IMediaEventEx mediaEventEx;

		// Token: 0x040000FC RID: 252
		private IBasicAudio basicAudio;

		// Token: 0x040000FD RID: 253
		private IMediaSeeking mediaSeeking;

		// Token: 0x040000FE RID: 254
		private IMediaPosition mediaPosition;

		// Token: 0x040000FF RID: 255
		private object lockObject = new object();

		// Token: 0x04000100 RID: 256
		private int currentVolume;

		// Token: 0x04000101 RID: 257
		private IntPtr hDrain = IntPtr.Zero;

		// Token: 0x04000102 RID: 258
		private string _fileName;

		// Token: 0x04000103 RID: 259
		private bool _audioPrepared;
	}
}
