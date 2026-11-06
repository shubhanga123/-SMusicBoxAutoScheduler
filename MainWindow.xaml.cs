using System;
using System.IO;
using System.Data;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using System.Diagnostics;

namespace SMusicBoxAutoScheduler
{
    public partial class MainWindow : Window
    {
        private static readonly string DbDir = AppPaths.DatabaseDirectory;
        private static readonly string DbPath = AppPaths.Database;
        private static readonly string DownloadDir = AppPaths.Downloads;
        private static readonly string CredentialDir = AppPaths.Credentials;

        private QueueBackgroundWorker _worker;
        private VideoIngestService _ingestService;

        public MainWindow()
        {
            InitializeComponent();
            EnsureDirectoriesAndDatabase();

            _worker = new QueueBackgroundWorker();
            _ingestService = new VideoIngestService();

            InitializeSchedulePicker();
            LoadDashboardData();
            CheckAuthStatus();

            _worker.Start();

            BtnStartWorker.Content = "⏸ Uploads Paused";
            BtnStartWorker.Foreground = Brushes.Orange;

            TxtStatus.Text =
                "Status: Ready. Automatic uploads are paused.";

            Closing += (_, _) => _worker.Stop();
        }

        // ==================================================
        // SCHEDULING
        // ==================================================

        private void InitializeSchedulePicker()
        {
            var nepalNow = DateTimeOffset.UtcNow.ToOffset(
                TimeSpan.FromMinutes(345));

            PickerPublishDate.SelectedDate = nepalNow.Date;

            for (int h = 0; h < 24; h++)
                ComboHour.Items.Add(h.ToString("D2"));

            for (int m = 0; m < 60; m += 5)
                ComboMinute.Items.Add(m.ToString("D2"));

            var target = nepalNow.AddHours(2);

            ComboHour.SelectedItem = target.Hour.ToString("D2");
            ComboMinute.SelectedItem =
                (target.Minute / 5 * 5).ToString("D2");

            UpdateSelectedSchedule();
        }

        private bool UpdateSelectedSchedule()
        {
            if (PickerPublishDate.SelectedDate == null ||
                ComboHour.SelectedItem == null ||
                ComboMinute.SelectedItem == null)
                return false;

            int hour = int.Parse(ComboHour.SelectedItem.ToString()!);
            int minute = int.Parse(ComboMinute.SelectedItem.ToString()!);

            var selected = PickerPublishDate.SelectedDate.Value.Date
                .AddHours(hour).AddMinutes(minute);

            var nepalTime = new DateTimeOffset(
                selected, TimeSpan.FromMinutes(345));

            if (nepalTime <= DateTimeOffset.UtcNow.ToOffset(
                TimeSpan.FromMinutes(345)))
            {
                LblSelectedSchedule.Text =
                    "Please select a future date and time.";
                LblSelectedSchedule.Foreground = Brushes.Orange;
                return false;
            }

            TxtScheduleTime.Text =
                nepalTime.ToString("yyyy-MM-ddTHH:mm:sszzz");

            LblSelectedSchedule.Text =
                "Publishing: " + nepalTime.ToString(
                    "dddd, dd MMMM yyyy — hh:mm tt") + " NPT";

            LblSelectedSchedule.Foreground = Brushes.LightGreen;
            return true;
        }

        private bool ValidateSelectedSchedule()
        {
            if (UpdateSelectedSchedule())
                return true;

            MessageBox.Show(
                "Please select a valid future publishing date and time.",
                "Invalid Schedule",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return false;
        }

        // ==================================================
        // DATABASE
        // ==================================================

        private void EnsureDirectoriesAndDatabase()
        {
            AppPaths.EnsureDirectories();
            DatabaseManager.InitializeDatabase();
        }

        private void CheckAuthStatus()
        {
            string secret = Path.Combine(
                CredentialDir, "client_secret.json");

            bool hasSecret = File.Exists(secret);

            bool hasToken = Directory.Exists(CredentialDir) &&
                Directory.GetFiles(
                    CredentialDir, "*token*",
                    SearchOption.AllDirectories).Length > 0;

            if (hasSecret && hasToken)
            {
                TxtAuthStatus.Text = "Connected & Active";
                TxtAuthStatus.Foreground = Brushes.LightGreen;
                BtnAuthConnect.Content = "Re-authenticate";
            }
            else if (hasSecret)
            {
                TxtAuthStatus.Text = "Secret Found (Needs Login)";
                TxtAuthStatus.Foreground = Brushes.Orange;
                BtnAuthConnect.Content = "Authenticate Now";
            }
            else
            {
                TxtAuthStatus.Text = "Missing client_secret.json";
                TxtAuthStatus.Foreground = Brushes.IndianRed;
                BtnAuthConnect.Content = "Setup Credentials";
            }
        }

        private void LoadDashboardData()
        {
            try
            {
                int total = 0;
                int pending = 0;
                int completed = 0;

                var dt = new DataTable();

                dt.Columns.Add("id", typeof(long));
                dt.Columns.Add("title", typeof(string));
                dt.Columns.Add("scheduled_time", typeof(string));
                dt.Columns.Add("status", typeof(string));
                dt.Columns.Add("youtube_id", typeof(string));
                dt.Columns.Add("thumbnail_path", typeof(string));

                var recent = new DataTable();
                recent.Columns.Add("ThumbnailImage", typeof(ImageSource));
                recent.Columns.Add("Title", typeof(string));
                recent.Columns.Add("ScheduledTime", typeof(string));
                recent.Columns.Add("Status", typeof(string));

                using var connection = new SqliteConnection(
                    $"Data Source={DbPath}");

                connection.Open();

                using var command = connection.CreateCommand();

                command.CommandText = @"
                    SELECT id, title, scheduled_time, status,
                           youtube_id, thumbnail_path
                    FROM upload_queue
                    ORDER BY id DESC;";

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    total++;

                    string title = reader.IsDBNull(1)
                        ? "Untitled" : reader.GetString(1);

                    string scheduled = reader.IsDBNull(2)
                        ? "" : reader.GetString(2);

                    string status = reader.IsDBNull(3)
                        ? "" : reader.GetString(3);

                    string youtubeId = reader.IsDBNull(4)
                        ? "" : reader.GetString(4);

                    string thumbnail = reader.IsDBNull(5)
                        ? "" : reader.GetString(5);

                    if (status == "pending") pending++;
                    if (status == "completed") completed++;

                    dt.Rows.Add(
                        reader.GetInt64(0),
                        title,
                        scheduled,
                        status,
                        youtubeId,
                        thumbnail);

                    if (recent.Rows.Count < 8)
                    {
                        ImageSource? image = null;

                        if (!string.IsNullOrWhiteSpace(thumbnail) &&
                            File.Exists(thumbnail))
                        {
                            try
                            {
                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.CacheOption =
                                    BitmapCacheOption.OnLoad;
                                bitmap.UriSource =
                                    new Uri(thumbnail);
                                bitmap.DecodePixelWidth = 180;
                                bitmap.EndInit();
                                bitmap.Freeze();
                                image = bitmap;
                            }
                            catch { }
                        }

                        recent.Rows.Add(
                            image,
                            title,
                            scheduled,
                            status);
                    }
                }

                LblStatTotal.Text = total.ToString();
                LblStatPending.Text = pending.ToString();
                LblStatCompleted.Text = completed.ToString();

                GridQueue.ItemsSource = dt.DefaultView;
                GridRecentVideos.ItemsSource = recent.DefaultView;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Dashboard error: " + ex.Message);
            }
        }

        private void LoadFileManagerData()
        {
            try
            {
                Directory.CreateDirectory(DownloadDir);

                var dt = new DataTable();

                dt.Columns.Add("File Name", typeof(string));
                dt.Columns.Add("Size (MB)", typeof(string));
                dt.Columns.Add("Created Date", typeof(string));
                dt.Columns.Add("Full Path", typeof(string));

                foreach (var file in new DirectoryInfo(
                    DownloadDir).GetFiles())
                {
                    double size = Math.Round(
                        file.Length / (1024.0 * 1024.0), 2);

                    dt.Rows.Add(
                        file.Name,
                        size.ToString(),
                        file.CreationTime.ToString(
                            "yyyy-MM-dd HH:mm:ss"),
                        file.FullName);
                }

                GridFiles.ItemsSource = dt.DefaultView;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void LoadHistoryData()
        {
            try
            {
                var dt = new DataTable();

                dt.Columns.Add("id", typeof(long));
                dt.Columns.Add("title", typeof(string));
                dt.Columns.Add("youtube_id", typeof(string));
                dt.Columns.Add("scheduled_time", typeof(string));

                using var connection = new SqliteConnection(
                    $"Data Source={DbPath}");

                connection.Open();

                using var command = connection.CreateCommand();

                command.CommandText = @"
                    SELECT id, title, youtube_id, scheduled_time
                    FROM upload_queue
                    WHERE status = 'completed'
                    ORDER BY id DESC;";

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    dt.Rows.Add(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        reader.GetString(3));
                }

                GridHistory.ItemsSource = dt.DefaultView;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        // ==================================================
        // NAVIGATION
        // ==================================================

        private void ShowView(string viewName, string title)
        {
            TxtViewTitle.Text = title;

            PanelDashboard.Visibility = Visibility.Collapsed;
            PanelQueue.Visibility = Visibility.Collapsed;
            PanelFiles.Visibility = Visibility.Collapsed;
            PanelHistory.Visibility = Visibility.Collapsed;
            PanelSettings.Visibility = Visibility.Collapsed;

            if (viewName == "Dashboard")
            {
                PanelDashboard.Visibility = Visibility.Visible;
                LoadDashboardData();
            }
            else if (viewName == "Queue")
            {
                PanelQueue.Visibility = Visibility.Visible;
                LoadDashboardData();
            }
            else if (viewName == "Files")
            {
                PanelFiles.Visibility = Visibility.Visible;
                LoadFileManagerData();
            }
            else if (viewName == "History")
            {
                PanelHistory.Visibility = Visibility.Visible;
                LoadHistoryData();
            }
            else if (viewName == "Settings")
                PanelSettings.Visibility = Visibility.Visible;
        }

        private void NavDashboard_Click(object sender, RoutedEventArgs e) =>
            ShowView("Dashboard", "Dashboard Overview");

        private void NavQueue_Click(object sender, RoutedEventArgs e) =>
            ShowView("Queue", "Upload Queue & Operations");

        private void NavFiles_Click(object sender, RoutedEventArgs e) =>
            ShowView("Files", "Downloaded File Management");

        private void NavHistory_Click(object sender, RoutedEventArgs e) =>
            ShowView("History", "Publishing History & Logs");

        private void NavSettings_Click(object sender, RoutedEventArgs e) =>
            ShowView("Settings", "Application Settings");

        // ==================================================
        // WORKER
        // ==================================================

        private void BtnToggleWorker_Click(
            object sender, RoutedEventArgs e)
        {
            if (_worker.IsPaused)
            {
                var result = MessageBox.Show(
                    "This enables automatic YouTube uploads. Continue?",
                    "Enable Automatic Uploads",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;

                _worker.Resume();
                BtnStartWorker.Content = "Uploads Active — Pause";
                BtnStartWorker.Foreground = Brushes.LightGreen;
                TxtStatus.Text = "Status: Automatic uploads enabled.";
            }
            else
            {
                _worker.Pause();
                BtnStartWorker.Content = "Uploads Paused — Resume";
                BtnStartWorker.Foreground = Brushes.Orange;

                TxtStatus.Text =
                    "Status: Paused. An active upload will finish.";
            }
        }

        // ==================================================
        // TEXT INPUT DIALOG
        // ==================================================

        private string? AskText(
            string title, string prompt, string initial = "")
        {
            var dialog = new Window
            {
                Title = title,
                Width = 520,
                Height = 260,
                WindowStartupLocation =
                    WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(
                    Color.FromRgb(37, 37, 38)),
                Foreground = Brushes.White,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new StackPanel { Margin = new Thickness(18) };

            panel.Children.Add(new TextBlock
            {
                Text = prompt,
                Margin = new Thickness(0, 0, 0, 10),
                Foreground = Brushes.White
            });

            var input = new TextBox
            {
                Text = initial,
                Padding = new Thickness(8),
                Background = new SolidColorBrush(
                    Color.FromRgb(30, 30, 30)),
                Foreground = Brushes.White,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility =
                    ScrollBarVisibility.Auto,
                Height = 90
            };

            panel.Children.Add(input);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 15, 0, 0)
            };

            string? answer = null;

            var cancel = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(18, 7, 18, 7),
                Margin = new Thickness(5)
            };

            var ok = new Button
            {
                Content = "Continue",
                Padding = new Thickness(18, 7, 18, 7),
                Background = Brushes.Teal,
                Foreground = Brushes.White,
                Margin = new Thickness(5),
                IsDefault = true
            };

            cancel.Click += (_, _) => dialog.DialogResult = false;

            ok.Click += (_, _) =>
            {
                answer = input.Text.Trim();
                dialog.DialogResult = true;
            };

            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            panel.Children.Add(buttons);

            dialog.Content = panel;

            return dialog.ShowDialog() == true ? answer : null;
        }

        // ==================================================
        // LAST STEP: NEPAL TIME SCHEDULER DIALOG
        // ==================================================

        private string? AskSchedule()
        {
            var dialog = new Window
            {
                Title = "Publishing Schedule — Nepal Time",
                Width = 480,
                Height = 300,
                WindowStartupLocation =
                    WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(
                    Color.FromRgb(37, 37, 38)),
                Foreground = Brushes.White,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new StackPanel
            {
                Margin = new Thickness(20)
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Choose the publishing date and time (NPT)",
                Margin = new Thickness(0, 0, 0, 12),
                FontSize = 14,
                FontWeight = FontWeights.Bold
            });

            var date = new DatePicker
            {
                SelectedDateFormat = DatePickerFormat.Long,
                Width = 240,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var now = DateTimeOffset.UtcNow.ToOffset(
                TimeSpan.FromMinutes(345));

            var target = now.AddHours(2);
            date.SelectedDate = target.Date;

            panel.Children.Add(date);

            var timePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 15, 0, 0)
            };

            var hour = new ComboBox
            {
                Width = 85,
                Height = 32
            };

            var minute = new ComboBox
            {
                Width = 85,
                Height = 32,
                Margin = new Thickness(10, 0, 0, 0)
            };

            for (int h = 0; h < 24; h++)
                hour.Items.Add(h.ToString("D2"));

            for (int m = 0; m < 60; m += 5)
                minute.Items.Add(m.ToString("D2"));

            hour.SelectedItem = target.Hour.ToString("D2");
            minute.SelectedItem =
                (target.Minute / 5 * 5).ToString("D2");

            timePanel.Children.Add(hour);
            timePanel.Children.Add(minute);

            panel.Children.Add(timePanel);

            var preview = new TextBlock
            {
                Margin = new Thickness(0, 15, 0, 10),
                Foreground = Brushes.LightGreen
            };

            panel.Children.Add(preview);

            void RefreshPreview()
            {
                if (date.SelectedDate == null ||
                    hour.SelectedItem == null ||
                    minute.SelectedItem == null)
                    return;

                var local = date.SelectedDate.Value.Date
                    .AddHours(int.Parse(hour.SelectedItem.ToString()!))
                    .AddMinutes(int.Parse(minute.SelectedItem.ToString()!));

                preview.Text = "Selected: " +
                    local.ToString("dddd, dd MMM yyyy — hh:mm tt") +
                    " NPT";
            }

            date.SelectedDateChanged += (_, _) => RefreshPreview();
            hour.SelectionChanged += (_, _) => RefreshPreview();
            minute.SelectionChanged += (_, _) => RefreshPreview();

            RefreshPreview();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            string? schedule = null;

            var cancel = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(5)
            };

            var confirm = new Button
            {
                Content = "Confirm Schedule",
                Padding = new Thickness(16, 7, 16, 7),
                Background = Brushes.Teal,
                Foreground = Brushes.White,
                Margin = new Thickness(5),
                IsDefault = true
            };

            cancel.Click += (_, _) => dialog.DialogResult = false;

            confirm.Click += (_, _) =>
            {
                if (date.SelectedDate == null ||
                    hour.SelectedItem == null ||
                    minute.SelectedItem == null)
                {
                    MessageBox.Show(
                        "Select a date and time first.");
                    return;
                }

                var local = date.SelectedDate.Value.Date
                    .AddHours(int.Parse(hour.SelectedItem.ToString()!))
                    .AddMinutes(int.Parse(minute.SelectedItem.ToString()!));

                var selected = new DateTimeOffset(
                    local, TimeSpan.FromMinutes(345));

                if (selected <= DateTimeOffset.UtcNow.AddMinutes(2))
                {
                    MessageBox.Show(
                        "Publishing time must be at least two minutes in the future.");
                    return;
                }

                schedule = selected.ToString(
                    "yyyy-MM-ddTHH:mm:sszzz");

                dialog.DialogResult = true;
            };

            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            panel.Children.Add(buttons);

            dialog.Content = panel;

            return dialog.ShowDialog() == true ? schedule : null;
        }

        // ==================================================
        // LOCAL VIDEO WIZARD
        // ==================================================

        private void BtnAddVideo_Click(
            object sender, RoutedEventArgs e)
        {
            var videoDialog = new OpenFileDialog
            {
                Title = "Select Local Video",
                Filter = "Video Files (*.mp4;*.mov)|*.mp4;*.mov"
            };

            if (videoDialog.ShowDialog() != true)
                return;

            string sourceVideo = videoDialog.FileName;

            string defaultTitle =
                Path.GetFileNameWithoutExtension(sourceVideo);

            string? title = AskText(
                "Step 2 — Video Title",
                "Enter the YouTube video title:",
                defaultTitle);

            if (title == null)
                return;

            if (string.IsNullOrWhiteSpace(title))
            {
                MessageBox.Show("Video title cannot be empty.");
                return;
            }

            string? description = AskText(
                "Step 3 — Video Description",
                "Enter the video description:");

            if (description == null)
                return;

            string? thumbnailPath = null;

            var thumbnailChoice = MessageBox.Show(
                "Would you like to select a thumbnail now?\n\nYes = Select Thumbnail\nNo = Add Later",
                "Step 4 — Thumbnail",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (thumbnailChoice == MessageBoxResult.Cancel)
                return;

            if (thumbnailChoice == MessageBoxResult.Yes)
            {
                var thumbnailDialog = new OpenFileDialog
                {
                    Title = "Select Thumbnail",
                    Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp"
                };

                if (thumbnailDialog.ShowDialog() != true)
                    return;

                thumbnailPath = thumbnailDialog.FileName;
            }

            // Scheduling is deliberately the last step.
            string? scheduledTime = AskSchedule();

            if (scheduledTime == null)
                return;

            try
            {
                Directory.CreateDirectory(DownloadDir);

                string extension = Path.GetExtension(sourceVideo);
                string uniqueName = Guid.NewGuid().ToString("N");

                string destinationVideo = Path.Combine(
                    DownloadDir, uniqueName + extension);

                File.Copy(sourceVideo, destinationVideo);

                string? destinationThumbnail = null;

                if (!string.IsNullOrWhiteSpace(thumbnailPath))
                {
                    string thumbnailExtension =
                        Path.GetExtension(thumbnailPath);

                    destinationThumbnail = Path.Combine(
                        DownloadDir,
                        uniqueName + thumbnailExtension);

                    File.Copy(thumbnailPath, destinationThumbnail);
                }

                using var connection = new SqliteConnection(
                    $"Data Source={DbPath}");

                connection.Open();

                using var command = connection.CreateCommand();

                command.CommandText = @"
                    INSERT INTO upload_queue
                    (
                        file_path,
                        title,
                        description,
                        thumbnail_path,
                        scheduled_time,
                        status,
                        retry_count
                    )
                    VALUES
                    (
                        @path,
                        @title,
                        @description,
                        @thumbnail,
                        @scheduled,
                        'pending',
                        0
                    );";

                command.Parameters.AddWithValue(
                    "@path", destinationVideo);

                command.Parameters.AddWithValue(
                    "@title", title);

                command.Parameters.AddWithValue(
                    "@description", description);

                command.Parameters.AddWithValue(
                    "@thumbnail",
                    (object?)destinationThumbnail ?? DBNull.Value);

                command.Parameters.AddWithValue(
                    "@scheduled", scheduledTime);

                command.ExecuteNonQuery();

                LoadDashboardData();

                TxtStatus.Text =
                    $"Status: {title} successfully added to queue.";

                MessageBox.Show(
                    "Your video has been added to the publishing queue.\n\nAutomatic uploads remain paused.",
                    "Video Queued",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not add video:\n" + ex.Message,
                    "Queue Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ==================================================
        // YOUTUBE LINK INGESTION
        // ==================================================

        private async void BtnIngestLink_Click(
            object sender, RoutedEventArgs e)
        {
            string url = TxtVideoLink.Text.Trim();

            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("Enter a YouTube video URL.");
                return;
            }

            if (!ValidateSelectedSchedule())
                return;

            ProgressBarMain.Value = 0;
            TxtProgressPercent.Text = "0%";
            TxtProgressDetail.Text = "Starting ingestion...";
            TxtStatus.Text = "Status: Processing link ingestion...";

            bool success =
                await _ingestService.IngestYouTubeLinkCustomScheduleAsync(
                    url,
                    TxtScheduleTime.Text.Trim(),
                    (message, percent) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            TxtProgressDetail.Text = message;
                            ProgressBarMain.Value = percent;
                            TxtProgressPercent.Text = $"{percent}%";
                        });
                    });

            if (success)
            {
                TxtVideoLink.Text = "";
                LoadDashboardData();
                TxtStatus.Text =
                    "Status: Video successfully added to queue.";
            }
            else
            {
                LoadDashboardData();
                TxtStatus.Text = "Status: Ingestion failed.";
            }
        }

        // ==================================================
        // THUMBNAIL MANAGEMENT
        // ==================================================

        private async void BtnUploadThumbnail_Click(
            object sender, RoutedEventArgs e)
        {
            if (GridQueue.SelectedItem is not DataRowView row)
            {
                MessageBox.Show("Select a video from the queue first.");
                return;
            }

            long id = Convert.ToInt64(row["id"]);

            string status = Convert.ToString(row["status"]) ?? "";
            string youtubeId =
                Convert.ToString(row["youtube_id"]) ?? "";

            if (status == "processing")
            {
                MessageBox.Show(
                    "This video is currently processing. Wait until it finishes.");
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Select Thumbnail",
                Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                string extension = Path.GetExtension(dialog.FileName);
                string destination = Path.Combine(
                    DownloadDir,
                    $"thumbnail_{id}_{Guid.NewGuid():N}{extension}");

                File.Copy(dialog.FileName, destination);

                if (status == "completed" &&
                    !string.IsNullOrWhiteSpace(youtubeId))
                {
                    TxtStatus.Text =
                        "Status: Uploading thumbnail to YouTube...";

                    var uploader = new YouTubeUploadService();

                    await uploader.SetThumbnailAsync(
                        youtubeId, destination);
                }

                using var connection = new SqliteConnection(
                    $"Data Source={DbPath}");

                connection.Open();

                using var command = connection.CreateCommand();

                command.CommandText = @"
                    UPDATE upload_queue
                    SET thumbnail_path = @thumbnail
                    WHERE id = @id
                      AND status <> 'processing';";

                command.Parameters.AddWithValue(
                    "@thumbnail", destination);

                command.Parameters.AddWithValue("@id", id);

                if (command.ExecuteNonQuery() != 1)
                    throw new Exception(
                        "Queue item changed before thumbnail could be saved.");

                LoadDashboardData();

                TxtStatus.Text =
                    "Status: Thumbnail attached successfully.";

                MessageBox.Show(
                    status == "completed"
                    ? "Thumbnail upload completed."
                    : "Thumbnail saved. It will be included when the video is uploaded.",
                    "Thumbnail Updated",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Thumbnail operation failed:\n" + ex.Message,
                    "Thumbnail Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ==================================================
        // QUEUE DELETION
        // ==================================================

        private void BtnDeleteQueueItem_Click(
            object sender, RoutedEventArgs e)
        {
            if (GridQueue.SelectedItem is not DataRowView row)
            {
                MessageBox.Show("Select a queue item first.");
                return;
            }

            long id = Convert.ToInt64(row["id"]);

            string status = Convert.ToString(row["status"]) ?? "";

            if (status == "processing")
            {
                MessageBox.Show(
                    "A video being processed cannot be deleted.");
                return;
            }

            if (MessageBox.Show(
                "Delete this queue entry? The video file will remain on disk.",
                "Confirm Deletion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            using var connection = new SqliteConnection(
                $"Data Source={DbPath}");

            connection.Open();

            using var command = connection.CreateCommand();

            command.CommandText = @"
                DELETE FROM upload_queue
                WHERE id = @id AND status <> 'processing';";

            command.Parameters.AddWithValue("@id", id);

            if (command.ExecuteNonQuery() != 1)
            {
                MessageBox.Show("Queue entry could not be deleted.");
                return;
            }

            LoadDashboardData();
            TxtStatus.Text = "Status: Queue entry deleted.";
        }

        // ==================================================
        // AUTHENTICATION
        // ==================================================

        private void BtnAuthConnect_Click(
            object sender, RoutedEventArgs e)
        {
            string secret = Path.Combine(
                CredentialDir, "client_secret.json");

            if (!File.Exists(secret))
            {
                MessageBox.Show(
                    "Place client_secret.json in:\n" + CredentialDir);

                Process.Start("explorer.exe", CredentialDir);
                return;
            }

            try
            {
                TxtStatus.Text = "Status: Connecting to Google...";

                var uploader = new YouTubeUploadService();
                uploader.ForceAuthenticationTest();

                MessageBox.Show(
                    "Google authentication successful.");

                CheckAuthStatus();
                TxtStatus.Text = "Status: Google connected.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Authentication error: " + ex.Message);
            }
        }

        // ==================================================
        // REFRESH AND FILE MANAGEMENT
        // ==================================================

        private void BtnRefresh_Click(
            object sender, RoutedEventArgs e)
        {
            LoadDashboardData();
            CheckAuthStatus();
            TxtStatus.Text = "Status: Dashboard refreshed.";
        }

        private void BtnRefreshFiles_Click(
            object sender, RoutedEventArgs e) =>
            LoadFileManagerData();

        private void BtnOpenDownloadsFolder_Click(
            object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(DownloadDir);
            Process.Start("explorer.exe", DownloadDir);
        }

        private void BtnOpenCredentialsFolder_Click(
            object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(CredentialDir);
            Process.Start("explorer.exe", CredentialDir);
        }
    }
}
