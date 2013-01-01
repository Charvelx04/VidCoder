﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VidCoder
{
	using System.Diagnostics;
	using System.Globalization;
	using System.IO;
	using System.ServiceModel;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Xml.Serialization;
	using HandBrake.Interop;
	using HandBrake.Interop.Model;
	using HandBrake.Interop.SourceData;
	using Model;
	using Properties;
	using Services;
	using VidCoderWorker;

	using Microsoft.Practices.Unity;
	using Timer = System.Timers.Timer;

	public class EncodeProxy : IHandBrakeEncoderCallback
	{
		// Ping interval (6s) longer than timeout (5s) so we don't have two overlapping pings
		private const double PingTimerIntervalMs = 6000;

		private const double PipeTimeoutSeconds = 5;

		public event EventHandler EncodeStarted;

		/// <summary>
		/// Fires for progress updates when encoding.
		/// </summary>
		public event EventHandler<EncodeProgressEventArgs> EncodeProgress;

		/// <summary>
		/// Fires when an encode has completed.
		/// </summary>
		public event EventHandler<EncodeCompletedEventArgs> EncodeCompleted;

		private DuplexChannelFactory<IHandBrakeEncoder> pipeFactory;
		private IHandBrakeEncoder channel;
		private ILogger logger;

		private ManualResetEventSlim encodeStartEvent;
		private ManualResetEventSlim encodeEndEvent;

		// Instance and lock only used when doing in-process encode (for debugging)
		private HandBrakeInstance instance;
		private object encodeLock = new object();

		// Timer that pings the worker process periodically to see if it's still alive.
		private Timer pingTimer;

		private bool encoding;

		// Lock to take before interacting with the encoder process or changing encoding state.
		private object encoderLock = new object();

		[XmlIgnore]
		public bool IsEncodeStarted { get; private set; }

		public void StartEncode(VCJob job, bool preview, int previewNumber, int previewSeconds, double overallSelectedLengthSeconds)
		{
//#if DEBUG
//            this.StartEncodeInProcess(job, preview, previewNumber, previewSeconds, overallSelectedLengthSeconds);
//            return;
//#endif

			this.logger = Unity.Container.Resolve<ILogger>();

			this.encodeStartEvent = new ManualResetEventSlim(false);
			this.encodeEndEvent = new ManualResetEventSlim(false);

			var task = new Task(() =>
			    {
					var startInfo = new ProcessStartInfo(
						"VidCoderWorker.exe",
						Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
					startInfo.RedirectStandardOutput = true;
					startInfo.UseShellExecute = false;
					startInfo.CreateNoWindow = true;
					Process worker = Process.Start(startInfo);

					// We don't set this any more because the thread priority inside the worker process sets them to lower priority.
			    	//worker.PriorityClass = ProcessPriorityClass.BelowNormal;

					// When the process writes out a line, its pipe server is ready and can be contacted for
					// work. Reading line blocks until this happens.
					worker.StandardOutput.ReadLine();

					lock (this.encoderLock)
					{
						this.ExecuteProxyOperation(() =>
							{
								var binding = new NetNamedPipeBinding
								{
									OpenTimeout = TimeSpan.FromSeconds(10),
									CloseTimeout = TimeSpan.FromSeconds(10),
									SendTimeout = TimeSpan.FromSeconds(10),
									ReceiveTimeout = TimeSpan.FromSeconds(10)
								};

								this.pipeFactory = new DuplexChannelFactory<IHandBrakeEncoder>(
									this,
									binding,
									new EndpointAddress("net.pipe://localhost/VidCoderWorker_" + worker.Id));

								this.channel = this.pipeFactory.CreateChannel();

								this.channel.StartEncode(job.HbJob, preview, previewNumber, previewSeconds, overallSelectedLengthSeconds,
														 Config.LogVerbosity, Config.PreviewCount);

								// After we do StartEncode (which can take a while), switch the timeout down to normal level to do pings
								var contextChannel = (IContextChannel)this.channel;
								contextChannel.OperationTimeout = TimeSpan.FromSeconds(PipeTimeoutSeconds);
							});
					}

			    	this.pingTimer = new Timer
					{
						AutoReset = true,
						Interval = PingTimerIntervalMs
					};

					this.pingTimer.Elapsed += (o, e) =>
					{
						lock (this.encoderLock)
						{
							if (!this.encoding)
							{
								return;
							}
						}

						if (this.encoding)
						{
							try
							{
								this.channel.Ping();
							}
							catch (CommunicationException exception)
							{
								lock (this.encoderLock)
								{
									this.logger.LogError("Worker process has crashed: " + exception.ToString());
									this.EndEncode(error: true);
								}
							}
							catch (TimeoutException exception)
							{
								lock (this.encoderLock)
								{
									this.logger.LogError("Worker process has crashed: " + exception.ToString());
									this.EndEncode(error: true);
								}
							}
						}
					};

					this.pingTimer.Start();
			    });

			this.encoding = true;
			task.Start();
		}

		private void StartEncodeInProcess(VCJob job, bool preview, int previewNumber, int previewSeconds, double overallSelectedLengthSeconds)
		{
			this.encoding = true;

			this.encodeStartEvent = new ManualResetEventSlim(false);
			this.encodeEndEvent = new ManualResetEventSlim(false);

			this.instance = new HandBrakeInstance();
			this.instance.Initialize(Config.LogVerbosity);

			this.instance.ScanCompleted += (o, e) =>
			{
				try
				{
					Title encodeTitle = this.instance.Titles.FirstOrDefault(title => title.TitleNumber == job.Title);

					if (encodeTitle != null)
					{
						lock (this.encodeLock)
						{
							this.instance.StartEncode(job.HbJob, preview, previewNumber, previewSeconds, overallSelectedLengthSeconds);
							this.OnEncodeStarted();
						}
					}
					else
					{
						this.OnEncodeComplete(true);
					}
				}
				catch (Exception exception)
				{
					this.OnException(exception.ToString());
				}
			};

			this.instance.EncodeProgress += (o, e) =>
			{
				this.OnEncodeProgress(e.AverageFrameRate, e.CurrentFrameRate, e.EstimatedTimeLeft, e.FractionComplete, e.Pass);
			};

			this.instance.EncodeCompleted += (o, e) =>
			{
				this.OnEncodeComplete(e.Error);
			};

			this.instance.StartScan(job.SourcePath, Config.PreviewCount, job.Title);
		}

		public void PauseEncode()
		{
			lock (this.encoderLock)
			{
				if (this.channel != null)
				{
					this.ExecuteProxyOperation(() => this.channel.PauseEncode());
				}
			}
		}

		public void ResumeEncode()
		{
			lock (this.encoderLock)
			{
				if (this.channel != null)
				{
					this.ExecuteProxyOperation(() => this.channel.ResumeEncode());
				}
			}
		}

		public void StopEncode()
		{
			lock (this.encoderLock)
			{
				if (this.channel != null)
				{
					this.ExecuteProxyOperation(() => this.channel.StopEncode());
				}
			}
		}

		// This can be called at any time: it will stop the encode ASAP and wait for encode to be stopped before returning.
		// Usually called before exiting the program.
		public void StopAndWait()
		{
			this.encodeStartEvent.Wait();

			lock (this.encoderLock)
			{
				if (this.encoding)
				{
					this.channel.StopEncode();
				}
			}

			this.encodeEndEvent.Wait();
		}

		public void OnEncodeStarted()
		{
			this.IsEncodeStarted = true;
			if (this.EncodeStarted != null)
			{
				this.EncodeStarted(this, new EventArgs());
			}

			this.encodeStartEvent.Set();
		}

		public void OnEncodeProgress(float averageFrameRate, float currentFrameRate, TimeSpan estimatedTimeLeft, float fractionComplete, int pass)
		{
			// Dispatch to avoid deadlocks on callbacks
			DispatchService.BeginInvoke(() =>
			    {
					lock (this.encoderLock)
					{
						if (this.encoding && this.EncodeProgress != null)
						{
							this.EncodeProgress(
								this,
								new EncodeProgressEventArgs
								{
									AverageFrameRate = averageFrameRate,
									CurrentFrameRate = currentFrameRate,
									EstimatedTimeLeft = estimatedTimeLeft,
									FractionComplete = fractionComplete,
									Pass = pass
								});
						}
					}
			    });
		}

		public void OnEncodeComplete(bool error)
		{
			lock (this.encoderLock)
			{
				this.EndEncode(error);
			}

			this.encodeEndEvent.Set();
		}

		public void OnMessageLogged(string message)
		{
			var entry = new LogEntry
			{
				LogType = LogType.Message,
				Source = LogSource.HandBrake,
				Text = message
			};

			this.logger.AddEntry(entry);
		}

		public void OnErrorLogged(string message)
		{
			var entry = new LogEntry
			{
				LogType = LogType.Error,
				Source = LogSource.HandBrake,
				Text = message
			};

			this.logger.AddEntry(entry);
		}

		public void OnException(string exceptionString)
		{
			this.logger.LogError("Encode worker crashed. Please report this error so it can be fixed in the future:" + Environment.NewLine + exceptionString);
		}

		// Executes the given proxy operation, stopping the encode and logging if a communication problem occurs.
		private void ExecuteProxyOperation(Action action)
		{
			try
			{
				action();
			}
			catch (CommunicationException exception)
			{
				this.StopEncodeWithError(exception);
			}
			catch (TimeoutException exception)
			{
				this.StopEncodeWithError(exception);
			}
		}

		private void EndEncode(bool error)
		{
			if (this.encoding)
			{
				if (this.EncodeCompleted != null)
				{
					this.EncodeCompleted(
						this,
						new EncodeCompletedEventArgs
							{
								Error = error
							});
				}

				if (this.pingTimer != null)
				{
					this.pingTimer.Dispose();
				}

				this.encoding = false;
			}
		}

		private void StopEncodeWithError(Exception exception)
		{
			this.logger.LogError("Unable to contact encode proxy: " + exception);
			this.EndEncode(error: true);
		}
	}
}