using System.IO;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using System.Diagnostics;
using System.Text;
using System.Windows.Input;
#if MACCATALYST
using AppKit;
using Foundation;
using Security;
using ObjCRuntime;
#endif

namespace DuckFinder;

public partial class MainPage : ContentPage
{
    private DirectoryInfo _currentDirectory = new DirectoryInfo("/");
    private FileSystemWatcher _watcher = new FileSystemWatcher();
    private Process? _currentProcess;
    private StringBuilder _terminalBuffer = new StringBuilder();
    private List<string> _commandHistory = new List<string>();
    private int _commandHistoryIndex = -1;
    private ICommand _terminalReturnCommand = null!;

    public ICommand TerminalReturnCommand => _terminalReturnCommand ??= new Command(OnTerminalInputCompleted);

    public MainPage()
    {
        InitializeComponent();
        _terminalReturnCommand = new Command(OnTerminalInputCompleted);
        _ = LoadDirectory(_currentDirectory);
        SetupSidebar();
        SetupWatcher();
        TerminalInput.TextChanged += OnTerminalInputTextChanged;
    }

    private void SetupSidebar()
    {
        var favorites = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        SidebarView.ItemsSource = favorites;
    }

    private void SetupWatcher()
    {
        _watcher.NotifyFilter = NotifyFilters.Attributes
                             | NotifyFilters.CreationTime
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.FileName
                             | NotifyFilters.LastAccess
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Security
                             | NotifyFilters.Size;

        _watcher.Changed += OnFileSystemChanged;
        _watcher.Created += OnFileSystemChanged;
        _watcher.Deleted += OnFileSystemChanged;
        _watcher.Renamed += OnFileSystemRenamed;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() => LoadDirectory(_currentDirectory).ConfigureAwait(false));
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() => LoadDirectory(_currentDirectory).ConfigureAwait(false));
    }

    private async Task LoadDirectory(DirectoryInfo directory)
    {
        if (!directory.Exists)
            return;

        _currentDirectory = directory;
        PathLabel.Text = directory.FullName;
        _watcher.Path = directory.FullName;

        try
        {
            var items = directory.GetFileSystemInfos()
                .Select(info => new FileSystemItem(info))
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name)
                .ToList();

            FileListView.ItemsSource = items;
        }
        catch (UnauthorizedAccessException)
        {
            await DisplayAlert("Error", "Access denied to this directory", "OK");
        }
    }

    private void OnTerminalInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (e.NewTextValue.Contains('\n'))
        {
            TerminalInput.Text = e.NewTextValue.Replace("\n", "");
            OnTerminalInputCompleted();
        }
    }

    private void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Handle file selection
    }

    private void OnSidebarSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Handle sidebar selection
    }

    private void OnBackClicked(object? sender, EventArgs e)
    {
        if (_currentDirectory.Parent != null)
        {
            LoadDirectory(_currentDirectory.Parent).ConfigureAwait(false);
        }
    }

    private void OnCopyPathClicked(object? sender, EventArgs e)
    {
        Clipboard.SetTextAsync(_currentDirectory.FullName).ConfigureAwait(false);
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Element element && element.BindingContext is FileSystemItem item)
        {
            if (item.IsDirectory)
            {
                LoadDirectory(new DirectoryInfo(item.Info.FullName)).ConfigureAwait(false);
            }
        }
    }

    private async void OnTerminalInputCompleted()
    {
        if (string.IsNullOrWhiteSpace(TerminalInput.Text))
        {
            TerminalInput.Focus();
            return;
        }

        string command = TerminalInput.Text.Trim();
        _commandHistory.Add(command);
        _commandHistoryIndex = _commandHistory.Count;
        TerminalInput.Text = string.Empty;

        var promptLabel = new Label
        {
            Text = $"{Environment.UserName}@{Environment.MachineName} {_currentDirectory.Name}$ {command}",
            TextColor = Colors.LightGreen,
            FontFamily = "Menlo",
            FontSize = 14
        };
        TerminalOutput.Children.Add(promptLabel);

        try
        {
            var processStartInfo = new ProcessStartInfo()
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _currentDirectory.FullName
            };

#if MACCATALYST
            // Add environment variables to ensure proper PATH for command execution
            processStartInfo.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
            // Set proper shell environment
            processStartInfo.Environment["SHELL"] = "/bin/bash";
#endif

            _currentProcess = new Process { StartInfo = processStartInfo };
            
            _currentProcess.OutputDataReceived += (s, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        var label = new Label
                        {
                            Text = args.Data,
                            TextColor = Colors.White,
                            FontFamily = "Menlo",
                            FontSize = 14
                        };
                        TerminalOutput.Children.Add(label);
                        await Task.Delay(10); // Wait for UI to update
                        await TerminalScrollView.ScrollToAsync(0, TerminalScrollView.ContentSize.Height, false);
                    });
                }
            };

            _currentProcess.ErrorDataReceived += (s, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        var label = new Label
                        {
                            Text = args.Data,
                            TextColor = Colors.Red,
                            FontFamily = "Menlo",
                            FontSize = 14
                        };
                        TerminalOutput.Children.Add(label);
                        await Task.Delay(10); // Wait for UI to update
                        await TerminalScrollView.ScrollToAsync(0, TerminalScrollView.ContentSize.Height, false);
                    });
                }
            };

            _currentProcess.Start();
            _currentProcess.BeginOutputReadLine();
            _currentProcess.BeginErrorReadLine();
            await _currentProcess.WaitForExitAsync();

            if (command.StartsWith("cd "))
            {
                string newPath = command.Substring(3).Trim();
                if (Path.IsPathRooted(newPath))
                {
                    await LoadDirectory(new DirectoryInfo(newPath));
                }
                else
                {
                    await LoadDirectory(new DirectoryInfo(Path.Combine(_currentDirectory.FullName, newPath)));
                }
            }
        }
        catch (Exception ex)
        {
            var errorLabel = new Label
            {
                Text = ex.Message,
                TextColor = Colors.Red,
                FontFamily = "Menlo",
                FontSize = 14
            };
            TerminalOutput.Children.Add(errorLabel);
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            TerminalInput.Focus();
            TerminalScrollView.ScrollToAsync(0, TerminalScrollView.ContentSize.Height, true);
        });
    }
    
    private async Task<bool> RequestUserFolderAccess(string folderPath)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select Folder"
            });

            return result != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error requesting folder access: {ex.Message}");
            return false;
        }
    }
}

#if MACCATALYST
[System.Runtime.Versioning.SupportedOSPlatform("maccatalyst")]
#endif
public class FileSystemItem
{
    public FileSystemInfo Info { get; }
    public string Name => Info.Name;
    public string FullPath => Info.FullName;
    public bool IsDirectory => Info is DirectoryInfo;
    public string Icon => IsDirectory ? "folder" : "document";

    public FileSystemItem(FileSystemInfo info)
    {
        Info = info;
    }
}

