﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandBrake.ApplicationServices.Interop;
using ReactiveUI;
using VidCoder.Extensions;
using VidCoder.Model;
using VidCoder.Resources;
using VidCoder.Services;
using VidCoder.Services.Windows;
using VidCoderCommon.Model;
using Geometry = HandBrake.ApplicationServices.Interop.Json.Shared.Geometry;

namespace VidCoder.ViewModel
{
	public class PreviewWindowViewModel : ReactiveObject, IClosableWindow
	{
		private const int PreviewImageCacheDistance = 1;
		private const double SubtitleScanCost = 1 / EncodeJobViewModel.SubtitleScanCostFactor;

		private static readonly TimeSpan MinPreviewImageRefreshInterval = TimeSpan.FromSeconds(0.5);
		private static int updateVersion;

		private VCJob job;
		private HandBrakeInstance originalScanInstance;
		private IEncodeProxy encodeProxy;
		private ILogger logger = Ioc.Get<ILogger>();
		private int selectedPreview;
		private string previewFilePath;
		private bool cancelPending;
		private bool encodeCancelled;
		private int previewSeconds;
		private int previewCount;

		private DateTime lastImageRefreshTime;
		private System.Timers.Timer previewImageRefreshTimer;
		private bool waitingOnRefresh;
		private BitmapSource[] previewImageCache;
		private Queue<PreviewImageJob> previewImageWorkQueue = new Queue<PreviewImageJob>();
		private bool previewImageQueueProcessing;
		private object imageSync = new object();
		private List<object> imageFileSync;
		private string imageFileCacheFolder;
		private BitmapSource previewBitmapSource;

		private MainViewModel mainViewModel = Ioc.Get<MainViewModel>();
		private OutputSizeService outputSizeService = Ioc.Get<OutputSizeService>();

		public PreviewWindowViewModel()
		{
			this.WhenAnyValue(
				x => x.SelectedPreview,
				x => x.MainViewModel.SelectedTitle,
				x => x.MainViewModel.Angle,
				x => x.OutputSizeService.Size,
				x => x.PresetsService.SelectedPreset.Preset.EncodingProfile.FlipHorizontal,
				x => x.PresetsService.SelectedPreset.Preset.EncodingProfile.FlipVertical,
				(selectedPreview, selectedTitle, angle, size, fliphorizontal, flipVertical) =>
				{
					return new object();
				}).Subscribe(x =>
				{
					this.RequestRefreshPreviews();
				});

			// PlayAvailable
			this.WhenAnyValue(x => x.MainViewModel.SourcePath, x => x.HasPreview, (sourcePath, hasPreview) =>
			{
				if (!hasPreview || sourcePath == null)
				{
					return false;
				}

				try
				{
					if (FileUtilities.IsDirectory(sourcePath))
					{
						// Path is a directory. Can only preview when it's a DVD and we have a supported player installed.
						bool isDvd = Utilities.IsDvdFolder(sourcePath);
						bool playerInstalled = Players.Installed.Count > 0;

						return isDvd && playerInstalled;
					}
					else
					{
						// Path is a file
						return true;
					}
				}
				catch (IOException)
				{
					this.RaisePropertyChanged(nameof(this.PlaySourceToolTip));
					return false;
				}
			}).ToProperty(this, x => x.PlayAvailable, out this.playAvailable);

			// SeekBarEnabled
			this.WhenAnyValue(x => x.HasPreview, x => x.GeneratingPreview, (hasPreview, generatingPreview) =>
			{
				return hasPreview && !generatingPreview;
			}).ToProperty(this, x => x.SeekBarEnabled, out this.seekBarEnabled);

			// PlaySourceToolTip
			this.WhenAnyValue(x => x.HasPreview, x => x.MainViewModel.SourcePath, (hasPreview, sourcePath) =>
			{
				if (!hasPreview || sourcePath == null)
				{
					return null;
				}

				try
				{
					if (FileUtilities.IsDirectory(sourcePath))
					{
						// Path is a directory. Can only preview when it's a DVD and we have a supported player installed.
						bool isDvd = Utilities.IsDvdFolder(sourcePath);
						if (!isDvd)
						{
							return PreviewRes.PlaySourceDisabledBluRayToolTip;
						}

						bool playerInstalled = Players.Installed.Count > 0;
						if (!playerInstalled)
						{
							return PreviewRes.PlaySourceDisabledNoPlayerToolTip;
						}
					}
				}
				catch (FileNotFoundException)
				{
					return PreviewRes.PlaySourceDisabledNotFoundToolTip;
				}
				catch (IOException)
				{
				}

				return PreviewRes.PlaySourceToolTip;
			}).ToProperty(this, x => x.PlaySourceToolTip, out this.playSourceToolTip);

			// SingleFitImageVisible
			this.WhenAnyValue(x => x.HasPreview, x => x.DisplayType, (hasPreview, displayType) =>
			{
				return hasPreview && displayType == PreviewDisplay.FitToWindow;
			}).ToProperty(this, x => x.SingleFitImageVisible, out this.singleFitImageVisible);

			// SingleOneToOneImageVisible
			this.WhenAnyValue(x => x.HasPreview, x => x.DisplayType, (hasPreview, displayType) =>
			{
				return hasPreview && displayType == PreviewDisplay.OneToOne;
			}).ToProperty(this, x => x.SingleOneToOneImageVisible, out this.singleOneToOneImageVisible);

			// CornersImagesVisible
			this.WhenAnyValue(x => x.HasPreview, x => x.DisplayType, (hasPreview, displayType) =>
			{
				return hasPreview && displayType == PreviewDisplay.Corners;
			}).ToProperty(this, x => x.CornersImagesVisible, out this.cornersImagesVisible);

			// InCornerDisplayMode
			this.WhenAnyValue(x => x.DisplayType)
				.Select(displayType => displayType == PreviewDisplay.Corners)
				.ToProperty(this, x => x.InCornerDisplayMode, out this.inCornerDisplayMode);

			// GeneratingPreview
			this.WhenAnyValue(x => x.EncodeState)
				.Select(encodeState => encodeState == PreviewEncodeState.EncodeStarting || encodeState == PreviewEncodeState.Encoding)
				.ToProperty(this, x => x.GeneratingPreview, out this.generatingPreview);

			this.PlaySource = ReactiveCommand.Create(this.WhenAnyValue(x => x.PlayAvailable));
			this.PlaySource.Subscribe(_ => this.PlaySourceImpl());

			this.GeneratePreview = ReactiveCommand.Create(this.WhenAnyValue(x => x.HasPreview));
			this.GeneratePreview.Subscribe(_ => this.GeneratePreviewImpl());

			this.CancelPreview = ReactiveCommand.Create(
				this.WhenAnyValue(x => x.EncodeState).Select(encodeState => encodeState == PreviewEncodeState.Encoding));
			this.CancelPreview.Subscribe(_ => this.CancelPreviewImpl());

			this.previewSeconds = Config.PreviewSeconds;
			this.displayType = CustomConfig.PreviewDisplay;
			this.selectedPreview = 1;
			this.Title = PreviewRes.NoVideoSourceTitle;

			this.RequestRefreshPreviews();
		}

		public IPreviewView View { get; set; }

		public void OnClosing()
		{
			if (this.GeneratingPreview)
			{
				this.StopAndWait();
			}
		}

		public MainViewModel MainViewModel
		{
			get
			{
				return this.mainViewModel;
			}
		}

		public ProcessingService ProcessingService { get; } = Ioc.Get<ProcessingService>();

		public PresetsService PresetsService { get; } = Ioc.Get<PresetsService>();

		public OutputPathService OutputPathVM { get; } = Ioc.Get<OutputPathService>();

		private OutputSizeService OutputSizeService
		{
			get { return this.outputSizeService; }
		}

		private string title;
		public string Title
		{
			get { return this.title; }
			set { this.RaiseAndSetIfChanged(ref this.title, value); }
		}

		private ImageSource previewImage;
		public ImageSource PreviewImage
		{
			get { return this.previewImage; }
			set { this.RaiseAndSetIfChanged(ref this.previewImage, value); }
		}

		public BitmapSource PreviewBitmapSource
		{
			get
			{
				lock (this.imageSync)
				{
					return this.previewBitmapSource;
				}
			}
		}

		private PreviewEncodeState encodeState;
		public PreviewEncodeState EncodeState
		{
			get { return this.encodeState; }
			set { this.RaiseAndSetIfChanged(ref this.encodeState, value); }
		}

		private ObservableAsPropertyHelper<bool> generatingPreview;
		public bool GeneratingPreview => this.generatingPreview.Value;

		private ObservableAsPropertyHelper<bool> inCornerDisplayMode;
		public bool InCornerDisplayMode => this.inCornerDisplayMode.Value;

		private ObservableAsPropertyHelper<bool> seekBarEnabled;
		public bool SeekBarEnabled => this.seekBarEnabled.Value;

		private double previewPercentComplete;
		public double PreviewPercentComplete
		{
			get { return this.previewPercentComplete; }
			set { this.RaiseAndSetIfChanged(ref this.previewPercentComplete, value); }
		}

		public int PreviewSeconds
		{
			get
			{
				return this.previewSeconds;
			}

			set
			{
				this.previewSeconds = value;
				this.RaisePropertyChanged();

				Config.PreviewSeconds = value;
			}
		}

		private bool hasPreview;
		public bool HasPreview
		{
			get { return this.hasPreview; }
			set { this.RaiseAndSetIfChanged(ref this.hasPreview, value); }
		}

		public int SelectedPreview
		{
			get
			{
				return this.selectedPreview;
			}

			set
			{
				this.selectedPreview = value;
				this.RaisePropertyChanged();

				if (this.DisplayType == PreviewDisplay.Corners)
				{
					this.RequestRefreshPreviews();
				}
				else
				{
					lock (this.imageSync)
					{
						this.previewBitmapSource = this.previewImageCache[value];
						this.RefreshFromBitmapImage();
						this.ClearOutOfRangeItems();
						this.BeginBackgroundImageLoad();
					}
				}
			}
		}

		private ObservableAsPropertyHelper<bool> singleFitImageVisible;
		public bool SingleFitImageVisible => this.singleFitImageVisible.Value;

		private ObservableAsPropertyHelper<bool> singleOneToOneImageVisible;
		public bool SingleOneToOneImageVisible => this.singleOneToOneImageVisible.Value;

		private ObservableAsPropertyHelper<bool> cornersImagesVisible;
		public bool CornersImagesVisible => this.cornersImagesVisible.Value;

		/// <summary>
		/// Gets or sets the display width of the preview image in pixels.
		/// </summary>
		public double PreviewDisplayWidth { get; set; }

		/// <summary>
		/// Gets or sets the display height of the preview image in pixels.
		/// </summary>
		public double PreviewDisplayHeight { get; set; }

		public int SliderMax
		{
			get
			{
				if (this.previewCount > 0)
				{
					return this.previewCount - 1;
				}

				return Config.PreviewCount - 1;
			}
		}

		public int PreviewCount
		{
			get
			{
				if (this.previewCount > 0)
				{
					return this.previewCount;
				}

				return Config.PreviewCount;
			}
		}

		private ObservableAsPropertyHelper<string> playSourceToolTip;
		public string PlaySourceToolTip => this.playSourceToolTip.Value;

		private ObservableAsPropertyHelper<bool> playAvailable;
		public bool PlayAvailable => this.playAvailable.Value;

		private PreviewDisplay displayType;
		public PreviewDisplay DisplayType
		{
			get
			{
				return this.displayType;
			}

			set
			{
				if (this.displayType != value)
				{
					this.displayType = value;
					this.RaisePropertyChanged();

					CustomConfig.PreviewDisplay = value;

					this.RequestRefreshPreviews();
				}
			}
		}

		public HandBrakeInstance ScanInstance
		{
			get
			{
				return this.mainViewModel.ScanInstance;
			}
		}

		public ReactiveCommand<object> GeneratePreview { get; }
		private void GeneratePreviewImpl()
		{
			this.job = this.mainViewModel.EncodeJob;

			this.PreviewPercentComplete = 0;
			this.EncodeState = PreviewEncodeState.EncodeStarting;
			this.cancelPending = false;
			this.encodeCancelled = false;

			this.SetPreviewFilePath();

			this.job.OutputPath = this.previewFilePath;

			this.encodeProxy = Utilities.CreateEncodeProxy();
			this.encodeProxy.EncodeStarted += (o, e) =>
			{
				DispatchUtilities.BeginInvoke(() =>
				{
					this.EncodeState = PreviewEncodeState.Encoding;
					if (this.cancelPending)
					{
						this.CancelPreviewImpl();
					}
				});
			};
			this.encodeProxy.EncodeProgress += (o, e) =>
			{
				double totalWeight;
				double completeWeight;
				if (e.PassCount == 1)
				{
					// Single pass, no subtitle scan
					totalWeight = 1;
					completeWeight = e.FractionComplete;
				}
				else if (e.PassCount == 2 && e.PassId <= 0)
				{
					// Single pass with subtitle scan
					totalWeight = 1 + SubtitleScanCost;
					if (e.PassId == -1)
					{
						// In subtitle scan
						completeWeight = e.FractionComplete * SubtitleScanCost;
					}
					else
					{
						// In normal pass
						completeWeight = SubtitleScanCost + e.FractionComplete;
					}
				}
				else if (e.PassCount == 2 && e.PassId >= 1)
				{
					// Two normal passes
					totalWeight = 2;

					if (e.PassId == 1)
					{
						// First pass
						completeWeight = e.FractionComplete;
					}
					else
					{
						// Second pass
						completeWeight = 1 + e.FractionComplete;
					}
				}
				else
				{
					// Two normal passes with subtitle scan
					totalWeight = 2 + SubtitleScanCost;

					if (e.PassId == -1)
					{
						// In subtitle scan
						completeWeight = e.FractionComplete * SubtitleScanCost;
					}
					else if (e.PassId == 1)
					{
						// First normal pass
						completeWeight = SubtitleScanCost + e.FractionComplete;
					}
					else
					{
						// Second normal pass
						completeWeight = SubtitleScanCost + 1 + e.FractionComplete;
					}
				}


				double fractionComplete = completeWeight / totalWeight;
				this.PreviewPercentComplete = fractionComplete * 100;
			};
			this.encodeProxy.EncodeCompleted += (o, e) =>
			{
				DispatchUtilities.BeginInvoke(() =>
				{
					this.EncodeState = PreviewEncodeState.NotEncoding;

					if (this.encodeCancelled)
					{
						this.logger.Log("Cancelled preview clip generation");
					}
					else
					{
						if (e.Error)
						{
							this.logger.Log(PreviewRes.PreviewClipGenerationFailedTitle);
							Utilities.MessageBox.Show(PreviewRes.PreviewClipGenerationFailedMessage);
						}
						else
						{
							var previewFileInfo = new FileInfo(this.previewFilePath);
							this.logger.Log("Finished preview clip generation. Size: " + Utilities.FormatFileSize(previewFileInfo.Length));

							FileService.Instance.PlayVideo(previewFilePath);
						}
					}
				});
			};

			this.logger.Log("Generating preview clip");
			this.logger.Log("  Path: " + this.job.OutputPath);
			this.logger.Log("  Title: " + this.job.Title);
			this.logger.Log("  Preview #: " + this.SelectedPreview);

			this.encodeProxy.StartEncode(this.job, this.logger, true, this.SelectedPreview, this.PreviewSeconds, this.job.Length.TotalSeconds);
		}

		private void SetPreviewFilePath()
		{
			string extension = OutputPathService.GetExtensionForProfile(this.job.EncodingProfile);

			string previewDirectory;
			if (Config.UseCustomPreviewFolder)
			{
				previewDirectory = Config.PreviewOutputFolder;
			}
			else
			{
				previewDirectory = Utilities.LocalAppFolder;
			}

			try
			{
				if (!Directory.Exists(previewDirectory))
				{
					Directory.CreateDirectory(previewDirectory);
				}
			}
			catch (Exception exception)
			{
				this.logger.LogError("Could not create preview directory " + Config.PreviewOutputFolder + Environment.NewLine + exception);
				previewDirectory = Utilities.LocalAppFolder;
			}

			this.previewFilePath = Path.Combine(previewDirectory, "preview" + extension);

			if (File.Exists(this.previewFilePath))
			{
				try
				{
					File.Delete(this.previewFilePath);
				}
				catch (Exception)
				{
					this.previewFilePath = FileUtilities.CreateUniqueFileName(this.previewFilePath, new HashSet<string>());
				}
			}
		}

		public ReactiveCommand<object> PlaySource { get; }
		private void PlaySourceImpl()
		{
			string sourcePath = this.mainViewModel.SourcePath;

			try
			{
				if (FileUtilities.IsDirectory(sourcePath))
				{
					// Path is a directory
					IVideoPlayer player = Players.Installed.FirstOrDefault(p => p.Id == Config.PreferredPlayer);
					if (player == null)
					{
						player = Players.Installed[0];
					}

					player.PlayTitle(sourcePath, this.mainViewModel.SelectedTitle.Index);
				}
				else
				{
					// Path is a file
					FileService.Instance.PlayVideo(sourcePath);
				}
			}
			catch (IOException)
			{
				this.RaisePropertyChanged(nameof(this.PlayAvailable));
			}
		}

		public ReactiveCommand<object> CancelPreview { get; }
		private void CancelPreviewImpl()
		{
			this.encodeCancelled = true;
			this.encodeProxy.StopEncode();
		}

		public void RequestRefreshPreviews()
		{
			if (!this.mainViewModel.HasVideoSource || this.outputSizeService.Size == null)
			{
				this.HasPreview = false;
				this.Title = PreviewRes.NoVideoSourceTitle;
				this.TryCancelPreviewEncode();
				return;
			}

			if (this.originalScanInstance != this.ScanInstance || (this.job != null && this.job.Title != this.mainViewModel.EncodeJob.Title))
			{
				this.TryCancelPreviewEncode();
			}

			if (this.waitingOnRefresh)
			{
				return;
			}

			DateTime now = DateTime.Now;
			TimeSpan timeSinceLastRefresh = now - this.lastImageRefreshTime;
			if (timeSinceLastRefresh < MinPreviewImageRefreshInterval)
			{
				this.waitingOnRefresh = true;
				TimeSpan timeUntilNextRefresh = MinPreviewImageRefreshInterval - timeSinceLastRefresh;
				this.previewImageRefreshTimer = new System.Timers.Timer(timeUntilNextRefresh.TotalMilliseconds);
				this.previewImageRefreshTimer.Elapsed += this.previewImageRefreshTimer_Elapsed;
				this.previewImageRefreshTimer.AutoReset = false;
				this.previewImageRefreshTimer.Start();

				return;
			}

			this.lastImageRefreshTime = now;

			this.RefreshPreviews();
		}

		private void StopAndWait()
		{
			this.encodeCancelled = true;
			this.encodeProxy.StopAndWait();
		}

		private void previewImageRefreshTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
		{
			this.waitingOnRefresh = false;
			this.lastImageRefreshTime = DateTime.MinValue;
			DispatchUtilities.BeginInvoke(this.RefreshPreviews);
		}

		private void RefreshPreviews()
		{
			this.originalScanInstance = this.ScanInstance;
			this.job = this.mainViewModel.EncodeJob;

			Geometry outputGeometry = this.outputSizeService.Size;

			int width = outputGeometry.Width;
			int height = outputGeometry.Height;
			int parWidth = outputGeometry.PAR.Num;
			int parHeight = outputGeometry.PAR.Den;

			if (parWidth <= 0 || parHeight <= 0)
			{
				this.HasPreview = false;
				this.Title = PreviewRes.NoVideoSourceTitle;

				Ioc.Get<ILogger>().LogError("HandBrake returned a negative pixel aspect ratio. Cannot show preview.");
				return;
			}

			if (width < 46 || height < 46)
			{
				this.HasPreview = false;
				this.UpdateTitle(outputGeometry);

				return;
			}

			this.PreviewDisplayHeight = height;
			this.PreviewDisplayWidth = width * ((double)parWidth / parHeight);

			// Update the number of previews.
			this.previewCount = this.ScanInstance.PreviewCount;
			if (this.selectedPreview >= this.previewCount)
			{
				this.selectedPreview = this.previewCount - 1;
				this.RaisePropertyChanged(nameof(this.SelectedPreview));
			}

			this.RaisePropertyChanged(nameof(this.PreviewCount));

			this.HasPreview = true;

			lock (this.imageSync)
			{
				this.previewImageCache = new BitmapSource[this.previewCount];
				updateVersion++;

				// Clear main work queue.
				this.previewImageWorkQueue.Clear();

				this.imageFileCacheFolder = Path.Combine(Utilities.ImageCacheFolder,
														 Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
														 updateVersion.ToString(CultureInfo.InvariantCulture));
				if (!Directory.Exists(this.imageFileCacheFolder))
				{
					Directory.CreateDirectory(this.imageFileCacheFolder);
				}

				// Clear old images out of the file cache.
				this.ClearImageFileCache();

				this.imageFileSync = new List<object>(this.previewCount);
				for (int i = 0; i < this.previewCount; i++)
				{
					this.imageFileSync.Add(new object());
				}

				this.BeginBackgroundImageLoad();
			}

			this.UpdateTitle(outputGeometry);
		}

		private void UpdateTitle(Geometry size)
		{
			if (size.PAR.Num == size.PAR.Den)
			{
				this.Title = string.Format(PreviewRes.PreviewWindowTitleSimple, size.Width, size.Height);
			}
			else
			{
				this.Title = string.Format(
					PreviewRes.PreviewWindowTitleComplex,
					Math.Round(this.PreviewDisplayWidth),
					Math.Round(this.PreviewDisplayHeight),
					size.Width,
					size.Height);
			}
		}

		private void ClearImageFileCache()
		{
			try
			{
				string processCacheFolder = Path.Combine(Utilities.ImageCacheFolder, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
				if (!Directory.Exists(processCacheFolder))
				{
					return;
				}

				int lowestUpdate = -1;
				for (int i = updateVersion - 1; i >= 1; i--)
				{
					if (Directory.Exists(Path.Combine(processCacheFolder, i.ToString(CultureInfo.InvariantCulture))))
					{
						lowestUpdate = i;
					}
					else
					{
						break;
					}
				}

				if (lowestUpdate == -1)
				{
					return;
				}

				for (int i = lowestUpdate; i <= updateVersion - 1; i++)
				{
					FileUtilities.DeleteDirectory(Path.Combine(processCacheFolder, i.ToString(CultureInfo.InvariantCulture)));
				}
			}
			catch (IOException)
			{
				// Ignore. Later checks will clear the cache.
			}
		}

		private void TryCancelPreviewEncode()
		{
			switch (this.EncodeState)
			{
				case PreviewEncodeState.NotEncoding:
					break;
				case PreviewEncodeState.EncodeStarting:
					this.cancelPending = true;
					break;
				case PreviewEncodeState.Encoding:
					this.CancelPreviewImpl();
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		private void ClearOutOfRangeItems()
		{
			// Remove out of range items from work queue
			var newWorkQueue = new Queue<PreviewImageJob>();
			while (this.previewImageWorkQueue.Count > 0)
			{
				PreviewImageJob job = this.previewImageWorkQueue.Dequeue();
				if (Math.Abs(job.PreviewNumber - this.SelectedPreview) <= PreviewImageCacheDistance)
				{
					newWorkQueue.Enqueue(job);
				}
			}

			// Remove out of range cache entries
			for (int i = 0; i < this.previewCount; i++)
			{
				if (Math.Abs(i - this.SelectedPreview) > PreviewImageCacheDistance)
				{
					this.previewImageCache[i] = null;
				}
			}
		}

		private void BeginBackgroundImageLoad()
		{
			int currentPreview = this.SelectedPreview;

			if (!ImageLoadedOrLoading(currentPreview))
			{
				this.EnqueueWork(currentPreview);
			}

			for (int i = 1; i <= PreviewImageCacheDistance; i++)
			{
				if (currentPreview - i >= 0 && !ImageLoadedOrLoading(currentPreview - i))
				{
					EnqueueWork(currentPreview - i);
				}

				if (currentPreview + i < this.previewCount && !ImageLoadedOrLoading(currentPreview + i))
				{
					EnqueueWork(currentPreview + i);
				}
			}

			// Start a queue processing thread if one is not going already.
			if (!this.previewImageQueueProcessing && this.previewImageWorkQueue.Count > 0)
			{
				ThreadPool.QueueUserWorkItem(this.ProcessPreviewImageWork);
				this.previewImageQueueProcessing = true;
			}
		}

		private bool ImageLoadedOrLoading(int previewNumber)
		{
			if (this.previewImageCache[previewNumber] != null)
			{
				return true;
			}

			if (this.previewImageWorkQueue.Count(j => j.PreviewNumber == previewNumber) > 0)
			{
				return true;
			}

			return false;
		}

		private void EnqueueWork(int previewNumber)
		{
			this.previewImageWorkQueue.Enqueue(new PreviewImageJob
			{
				UpdateVersion = updateVersion, ScanInstance = this.ScanInstance, PreviewNumber = previewNumber, Profile = this.job.EncodingProfile, Title = this.MainViewModel.SelectedTitle, ImageFileSync = this.imageFileSync[previewNumber]
			});
		}

		private void ProcessPreviewImageWork(object state)
		{
			PreviewImageJob imageJob;
			bool workLeft = true;

			while (workLeft)
			{
				lock (this.imageSync)
				{
					if (this.previewImageWorkQueue.Count == 0)
					{
						this.previewImageQueueProcessing = false;
						return;
					}

					imageJob = this.previewImageWorkQueue.Dequeue();
					while (imageJob.UpdateVersion < updateVersion)
					{
						if (this.previewImageWorkQueue.Count == 0)
						{
							this.previewImageQueueProcessing = false;
							return;
						}

						imageJob = this.previewImageWorkQueue.Dequeue();
					}
				}

				string imagePath = Path.Combine(Utilities.ImageCacheFolder, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture), imageJob.UpdateVersion.ToString(CultureInfo.InvariantCulture), imageJob.PreviewNumber + ".bmp");
				BitmapSource imageSource = null;

				// Check the disc cache for the image
				lock (imageJob.ImageFileSync)
				{
					if (File.Exists(imagePath))
					{
						// When we read from disc cache the image is already transformed.
						var bitmapImage = new BitmapImage();
						bitmapImage.BeginInit();
						bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
						bitmapImage.UriSource = new Uri(imagePath);
						bitmapImage.EndInit();
						bitmapImage.Freeze();

						imageSource = bitmapImage;
					}
				}

				if (imageSource == null && !imageJob.ScanInstance.IsDisposed)
				{
					// Make a HandBrake call to get the image
					imageSource = imageJob.ScanInstance.GetPreview(imageJob.Profile.CreatePreviewSettings(imageJob.Title), imageJob.PreviewNumber);

					// Transform the image as per rotation and reflection settings
					VCProfile profile = imageJob.Profile;
					if (profile.FlipHorizontal || profile.FlipVertical || profile.Rotation != VCPictureRotation.None)
					{
						imageSource = CreateTransformedBitmap(imageSource, profile);
					}

					// Start saving the image file in the background and continue to process the queue.
					ThreadPool.QueueUserWorkItem(this.BackgroundFileSave, new SaveImageJob
					{
						PreviewNumber = imageJob.PreviewNumber, UpdateVersion = imageJob.UpdateVersion, FilePath = imagePath, Image = imageSource, ImageFileSync = imageJob.ImageFileSync
					});
				}

				lock (this.imageSync)
				{
					if (imageJob.UpdateVersion == updateVersion)
					{
						this.previewImageCache[imageJob.PreviewNumber] = imageSource;
						if (this.SelectedPreview == imageJob.PreviewNumber)
						{
							DispatchUtilities.BeginInvoke(() =>
							{
								this.previewBitmapSource = imageSource;
								this.RefreshFromBitmapImage();
							});
						}
					}

					if (this.previewImageWorkQueue.Count == 0)
					{
						workLeft = false;
						this.previewImageQueueProcessing = false;
					}
				}
			}
		}

		private static TransformedBitmap CreateTransformedBitmap(BitmapSource source, VCProfile profile)
		{
			var transformedBitmap = new TransformedBitmap();
			transformedBitmap.BeginInit();
			transformedBitmap.Source = source;
			var transformGroup = new TransformGroup();
			transformGroup.Children.Add(new ScaleTransform(profile.FlipHorizontal ? -1 : 1, profile.FlipVertical ? -1 : 1));
			transformGroup.Children.Add(new RotateTransform(ConvertRotationToDegrees(profile.Rotation)));
			transformedBitmap.Transform = transformGroup;
			transformedBitmap.EndInit();
			transformedBitmap.Freeze();

			return transformedBitmap;
		}

		private static double ConvertRotationToDegrees(VCPictureRotation rotation)
		{
			switch (rotation)
			{
				case VCPictureRotation.None:
					return 0;
				case VCPictureRotation.Clockwise90:
					return 90;
				case VCPictureRotation.Clockwise180:
					return 180;
				case VCPictureRotation.Clockwise270:
					return 270;
			}

			return 0;
		}

		/// <summary>
		/// Refreshes the view using this.previewBitmapSource.
		/// </summary>
		private void RefreshFromBitmapImage()
		{
			if (this.previewBitmapSource == null)
			{
				return;
			}

			if (this.DisplayType != PreviewDisplay.Corners)
			{
				this.PreviewImage = this.previewBitmapSource;
			}

			// In the Corners display mode, the view code will react to the message and read from this.previewBitmapSource.
			this.View.RefreshImageSize();
		}

		private void BackgroundFileSave(object state)
		{
			var job = state as SaveImageJob;

			lock (this.imageSync)
			{
				if (job.UpdateVersion < updateVersion)
				{
					return;
				}
			}

			lock (job.ImageFileSync)
			{
				try
				{
					using (var memoryStream = new MemoryStream())
					{
						// Write the bitmap out to a memory stream before saving so that we won't be holding
						// a write lock on the BitmapImage for very long; it's used in the UI.
						var encoder = new BmpBitmapEncoder();
						encoder.Frames.Add(BitmapFrame.Create(job.Image));
						encoder.Save(memoryStream);

						using (var fileStream = new FileStream(job.FilePath, FileMode.Create))
						{
							fileStream.Write(memoryStream.GetBuffer(), 0, (int) memoryStream.Length);
						}
					}
				}
				catch (IOException)
				{
					// Directory may have been deleted. Ignore.
				}
			}
		}
	}
}