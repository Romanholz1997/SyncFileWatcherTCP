using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Timers;


class TcpClientApp
{
    private static TcpClient _Client;
    private static NetworkStream _Stream;
    private static bool _isSyncing = false;
    private static readonly object SyncLock = new object();
    private static readonly string FolderPathToWatch = @"C:\Share";
    private static readonly Dictionary<string, bool> PathIsDirectory = new Dictionary<string, bool>();
    private static readonly object PathIsDirectoryLock = new object();
    private static readonly int Port = 5000;
    private static readonly string ClientAddress = "135.181.162.240";
    //private static readonly string ClientAddress = "127.0.0.1";



    static FileSystemWatcher watcher;
    private static readonly ConcurrentDictionary<string, DateTime> ChangeEvents = new ConcurrentDictionary<string, DateTime>();
    private static readonly System.Timers.Timer DebounceTimer = new System.Timers.Timer(500); // 500ms debounce interval
    private static readonly object DebounceLock = new object();

    static void Main()
    {
        try
        {
            // Connect to the Client (replace "127.0.0.1" with Client IP if different)
            _Client = new TcpClient(ClientAddress, Port);
            _Stream = _Client.GetStream();
            Console.WriteLine("Connected to Client.");

            // Start a thread to listen for Client messages
            Thread listenThread = new Thread(ListenHandleMessage);
            listenThread.Start();

            
            //ListenHandleMessage();
            // Ensure the synchronization folder exists
            if (!Directory.Exists(FolderPathToWatch))
            {
                Directory.CreateDirectory(FolderPathToWatch);
            }
            InitializePathCache();
            // Initialize FileSystemWatcher
            watcher = new FileSystemWatcher
            {
                Path = FolderPathToWatch,
                IncludeSubdirectories = true, // Monitor subdirectories
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024 // Increase buffer size to 64 KB
            };

            // Subscribe to events
            watcher.Created += OnCreated;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;

            // Start watching
            watcher.EnableRaisingEvents = true;

            DebounceTimer.Elapsed += OnDebounceTimerElapsed;
            DebounceTimer.AutoReset = false;

            Console.WriteLine($"Watching for changes in {FolderPathToWatch}...");
            Console.WriteLine("Press 'q' to quit the application.");

            // Keep the application running until 'q' is pressed
            while (Console.Read() != 'q') ;

            // Cleanup
            _Stream.Close();
            _Client.Close();
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"SocketException: {ex.Message}");
        }
    }
    private static void InitializePathCache()
    {
        lock (PathIsDirectoryLock)
        {
            PathIsDirectory.Clear();
            foreach (string dir in Directory.GetDirectories(FolderPathToWatch, "*", SearchOption.AllDirectories))
            {
                PathIsDirectory[dir] = true;
            }
            foreach (string file in Directory.GetFiles(FolderPathToWatch, "*", SearchOption.AllDirectories))
            {
                PathIsDirectory[file] = false;
            }
        }
    }
    public static void ListenHandleMessage()
    {
        try
        {
            NetworkStream stream = _Stream;
            while (true)
            {
                // Read the command line from the client
                
                string commandLine = ReadLineFromStream(stream);
                
                if (string.IsNullOrEmpty(commandLine))
                {
                    // Client disconnected
                    break;
                }

                string command = commandLine.Trim();

                if (command.StartsWith("CHANGE_FILE "))
                {
                    Console.WriteLine($"Recieved Message {command}");
                    string commandArgs = command.Substring("CHANGE_FILE ".Length).Trim();
                    HandleChangeFile(commandArgs, stream);
                    command = "";

                }
                else if (command.StartsWith("RENAME_FILE "))
                {
                    Console.WriteLine($"Recieved Message {command}");
                    string commandArgs = command.Substring("RENAME_FILE ".Length).Trim();
                    HandleRenameFile(commandArgs);
                    command = "";
                }
                else if (command.StartsWith("DELETE_FILE "))
                {
                    Console.WriteLine($"Recieved Message {command}");
                    string commandArgs = command.Substring("DELETE_FILE ".Length).Trim();
                    HandleDeleteFile(commandArgs);
                    command = "";
                }
                else if (command.StartsWith("CREATE_DIRECTORY "))
                {
                    Console.WriteLine($"Recieved Message {command}");
                    string commandArgs = command.Substring("CREATE_DIRECTORY ".Length).Trim();
                    HandleCreateDirectory(commandArgs);
                   
                }
                else if (command.StartsWith("RENAME_DIRECTORY "))
                {
                    Console.WriteLine($"Recieved Message {command}");
                    string commandArgs = command.Substring("RENAME_DIRECTORY ".Length).Trim();
                    HandleRenameDirectory(commandArgs);
                    command = "";
                }
                else if (command.StartsWith("DELETE_DIRECTORY "))
                {
                    Console.WriteLine($"Recieved Message {command}");
                    string commandArgs = command.Substring("DELETE_DIRECTORY ".Length).Trim();
                    HandleDeleteDirectory(commandArgs);
                    command = "";
                }
                else
                {
                    Console.WriteLine($"[Client][{FolderPathToWatch}] Invalid command received: {command}");
                    command = "";
                }                
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Client][{FolderPathToWatch}] Error handling client: {ex.Message}");
        }
        finally
        {
            _Client.Close();
            _Stream.Close();
            Console.WriteLine($"[Client][{FolderPathToWatch}] Client disconnected.");
        }
    }
    private static void HandleChangeFile(string commandArgs, NetworkStream stream)
    {
        // Regular expression to match a quoted string and file size
        Regex regex = new Regex("^\"([^\"]+)\"\\s+(\\d+)$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            watcher.EnableRaisingEvents = false;
            //SetSyncing(true); // Reset IsSyncing to false
            string fileName = match.Groups[1].Value;
            string fileSizeStr = match.Groups[2].Value;

            if (!long.TryParse(fileSizeStr, out long fileSize))
            {
                Console.WriteLine($"[Client][{FolderPathToWatch}] Invalid file size.");
                return;
            }

            Console.WriteLine($"[Client][{FolderPathToWatch}] Receiving file: {fileName}, size: {fileSize}");

            // Specify the path to save the uploaded file
            string savePath = Path.Combine(FolderPathToWatch, fileName);
            try
            {
                // Ensure the directory exists
                string directoryPath = Path.GetDirectoryName(savePath);
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                // Create a FileStream to save the uploaded file
                using (FileStream fs = new FileStream(savePath, FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[4096];
                    long totalBytesRead = 0;
                    int readBytes;

                    // Read the file data based on the file size
                    while (totalBytesRead < fileSize && (readBytes = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        fs.Write(buffer, 0, readBytes);
                        totalBytesRead += readBytes;
                    }

                    if (totalBytesRead != fileSize)
                    {
                        Console.WriteLine($"[Client][{FolderPathToWatch}] Warning: Expected file size does not match the received data.");
                    }
                }

                Console.WriteLine($"[Client][{FolderPathToWatch}] File received and saved as: {savePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client][{FolderPathToWatch}] Error Saved file: {ex.Message}");
            }
            finally
            {
                watcher.EnableRaisingEvents = true;
            }
        }
        else
        {
            Console.WriteLine($"[Client][{FolderPathToWatch}] Invalid UPLOAD_FILE command format.");
        }
    }
    private static void HandleRenameFile(string commandArgs)
    {
        // Regular expression to match two quoted strings
        Regex regex = new Regex("^\"([^\"]+)\"\\s+\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string oldFileName = match.Groups[1].Value;
            string newFileName = match.Groups[2].Value;

            string oldFilePath = Path.Combine(FolderPathToWatch, oldFileName);
            string newFilePath = Path.Combine(FolderPathToWatch, newFileName);

            watcher.EnableRaisingEvents = false;
            try
            {
                if (File.Exists(oldFilePath))
                {
                    // Ensure the target directory exists
                    string newDirectoryPath = Path.GetDirectoryName(newFilePath);
                    if (!Directory.Exists(newDirectoryPath))
                    {
                        Directory.CreateDirectory(newDirectoryPath);
                    }

                    File.Move(oldFilePath, newFilePath);
                    Console.WriteLine($"[Client][{FolderPathToWatch}] File renamed from {oldFileName} to {newFileName}");
                }
                else
                {
                    Console.WriteLine($"[Client][{FolderPathToWatch}] File not found: {oldFileName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client][{FolderPathToWatch}] Error renaming file: {ex.Message}");
            }
            finally
            {
                watcher.EnableRaisingEvents = true;
            }
        }
        else
        {
            Console.WriteLine($"[Client][{FolderPathToWatch}] Invalid RENAME_FILE command format.");
        }
    }

    private static void HandleDeleteFile(string commandArgs)
    {
        // Regular expression to match a quoted string
        Regex regex = new Regex("^\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string fileName = match.Groups[1].Value;
            string filePath = Path.Combine(FolderPathToWatch, fileName);

            watcher.EnableRaisingEvents = false;
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Console.WriteLine($"[Client][{FolderPathToWatch}] File deleted: {fileName}");
                }
                else
                {
                    Console.WriteLine($"[Client][{FolderPathToWatch}] File not found: {fileName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client][{FolderPathToWatch}] Error deleting file: {ex.Message}");
            }
            finally
            {
                watcher.EnableRaisingEvents = true;
            }
        }
        else
        {
            Console.WriteLine($"[Client][{FolderPathToWatch}] Invalid DELETE_FILE command format.");
        }
    }

    private static void HandleCreateDirectory(string commandArgs)
    {
        // Regular expression to match a quoted directory path
        Regex regex = new Regex("^\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string relativePath = match.Groups[1].Value;
            string directoryPath = Path.Combine(FolderPathToWatch, relativePath);

            watcher.EnableRaisingEvents = false;
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                    Console.WriteLine($"[Client][{FolderPathToWatch}] Directory created: {directoryPath}");
                }
                else
                {
                    Console.WriteLine($"[Client][{FolderPathToWatch}] Directory already exists: {directoryPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client][{FolderPathToWatch}] Error creating directory: {ex.Message}");
            }
            finally
            {
                watcher.EnableRaisingEvents = true;
            }
        }
        else
        {
            Console.WriteLine($"[Client][{FolderPathToWatch}] Invalid CREATE_DIRECTORY command format.");
        }
    }

    private static void HandleRenameDirectory(string commandArgs)
    {
        // Regular expression to match two quoted directory paths
        Regex regex = new Regex("^\"([^\"]+)\"\\s+\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string oldRelativePath = match.Groups[1].Value;
            string newRelativePath = match.Groups[2].Value;
            string oldDirectoryPath = Path.Combine(FolderPathToWatch, oldRelativePath);
            string newDirectoryPath = Path.Combine(FolderPathToWatch, newRelativePath);

            watcher.EnableRaisingEvents = false;
            try
            {
                if (Directory.Exists(oldDirectoryPath))
                {
                    Directory.Move(oldDirectoryPath, newDirectoryPath);
                    Console.WriteLine($"[Client][{FolderPathToWatch}] Directory renamed from {oldRelativePath} to {newRelativePath}");
                }
                else
                {
                    Console.WriteLine($"[Client][{FolderPathToWatch}] Directory not found: {oldRelativePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client][{FolderPathToWatch}] Error renaming directory: {ex.Message}");
            }
            finally
            {
                watcher.EnableRaisingEvents = true;
            }
        }
        else
        {
            Console.WriteLine($"[Client][{FolderPathToWatch}] Invalid RENAME_DIRECTORY command format.");
        }
    }

    private static void HandleDeleteDirectory(string commandArgs)
    {
        // Regular expression to match a quoted directory path
        Regex regex = new Regex("^\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string relativePath = match.Groups[1].Value;
            string directoryPath = Path.Combine(FolderPathToWatch, relativePath);

            watcher.EnableRaisingEvents = false;
            try
            {
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, true);
                    Console.WriteLine($"[Client][{FolderPathToWatch}] Directory deleted: {directoryPath}");
                }
                else
                {
                    Console.WriteLine($"[Client][{FolderPathToWatch}] Directory not found: {directoryPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Client][{FolderPathToWatch}] Error deleting directory: {ex.Message}");
            }
            finally
            {
                watcher.EnableRaisingEvents = true;
            }
        }
        else
        {
            Console.WriteLine($"[Client][{FolderPathToWatch}] Invalid DELETE_DIRECTORY command format.");
        }
    }
    private static string ReadLineFromStream(NetworkStream stream)
    {
        StringBuilder sb = new StringBuilder();
        byte[] buffer = new byte[1];
        while (true)
        {
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                // Connection closed
                break;
            }
            char ch = (char)buffer[0];
            if (ch == '\n')
            {
                break;
            }
            else if (ch != '\r') // Ignore carriage return
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private static void HandleFileSystemEvent(FileSystemEventArgs e)
    {
        if (Directory.Exists(e.FullPath))
            return; // Skip directories

        // Log the event
        Console.WriteLine($"Change detected: {e.ChangeType} on {e.FullPath}");

        // Add or update the event timestamp
        ChangeEvents.AddOrUpdate(e.FullPath, DateTime.Now, (key, oldValue) => DateTime.Now);

        // Reset the debounce timer
        lock (DebounceLock)
        {
            DebounceTimer.Stop();
            DebounceTimer.Start();
        }
    }

    private static void OnDebounceTimerElapsed(object sender, ElapsedEventArgs e)
    {
        foreach (var change in ChangeEvents)
        {
            // Process the change
            Console.WriteLine($"Processing change for: {change.Key}");
            HandleFileChange(change.Key);
        }

        // Clear the tracked events
        ChangeEvents.Clear();
    }

    private static void OnCreated(object sender, FileSystemEventArgs e)
    {       
        bool isDirectory = Directory.Exists(e.FullPath);
        lock (PathIsDirectoryLock)
        {
            PathIsDirectory[e.FullPath] = isDirectory;
        }
        if (isDirectory)
        {
            // Handle directory creation                
            Console.WriteLine($"Directory Created {e.FullPath}");
            HandleDirectoryCreated(e.FullPath);
        }
        else if (File.Exists(e.FullPath))
        {
            // Handle file creation
            Console.WriteLine($"File Created {e.FullPath}");
            HandleFileChange(e.FullPath);
        }
    }

    private static void OnChanged(object sender, FileSystemEventArgs e)
    {        
        if (Directory.Exists(e.FullPath))
            return; // Changed events for directories are not handled
        lock (PathIsDirectoryLock)
        {
            PathIsDirectory[e.FullPath] = false;
        }
        if (File.Exists(e.FullPath))
        {
            // Handle file change
            Console.WriteLine($"File Changed {e.FullPath}");
            HandleFileSystemEvent(e);
        }
    }

    private static void OnDeleted(object sender, FileSystemEventArgs e)
    {      
        // Determine if it's a directory or file by checking if the path was a directory before deletion
        bool isDirectory = false;
        lock (PathIsDirectoryLock)
        {
            if (PathIsDirectory.TryGetValue(e.FullPath, out isDirectory))
            {
                PathIsDirectory.Remove(e.FullPath);

                // Remove all subpaths if it's a directory
                if (isDirectory)
                {
                    var keysToRemove = new List<string>();
                    foreach (var key in PathIsDirectory.Keys)
                    {
                        if (key.StartsWith(e.FullPath + Path.DirectorySeparatorChar))
                        {
                            keysToRemove.Add(key);
                        }
                    }

                    foreach (var key in keysToRemove)
                    {
                        PathIsDirectory.Remove(key);
                    }
                }
            }
            else
            {
                // Cannot determine if the deleted item was a file or directory
                Console.WriteLine($"Deleted item not found in path cache: {e.FullPath}");
            }
        }

        if (isDirectory)
        {

            Console.WriteLine($"Directory Deleted {e.FullPath}");
            HandleDirectoryDelete(e.FullPath);
        }
        else
        {
            // Handle file deletion
            Console.WriteLine($"File Deleted {e.FullPath}");
            HandleFileDelete(e.FullPath);
        }
    }

    private static void OnRenamed(object sender, RenamedEventArgs e)
    {
        bool isDirectory = false;
        lock (PathIsDirectoryLock)
        {
            if (PathIsDirectory.TryGetValue(e.OldFullPath, out isDirectory))
            {
                PathIsDirectory.Remove(e.OldFullPath);
                PathIsDirectory[e.FullPath] = isDirectory;
            }
            else
            {
                // Item not found in the dictionary
                Console.WriteLine($"Renamed item was not found in dictionary: {e.OldFullPath}");
                return;
            }
        }
        // Determine if it's a directory or file
        if (isDirectory)
        {
            // Handle directory rename
            Console.WriteLine($"Directory Renamed {e.FullPath}");
            HandleDirectoryRename(e.OldFullPath, e.FullPath);
        }
        else
        {
            // Handle file rename
            Console.WriteLine($"File Renamed {e.FullPath}");
            HandleFileRename(e.OldFullPath, e.FullPath);
        }
    }
    private static void HandleFileChange(string filePath)
    {
        try
        {

            // Get the relative file path
            Thread.Sleep(1000);
            string relativePath = Path.GetRelativePath(FolderPathToWatch, filePath).Replace("\\", "/");

            // Get the file size
            FileInfo fileInfo = new FileInfo(filePath);
            long fileSize = fileInfo.Length;

            // Construct the command
            string commandString = $"CHANGE_FILE \"{relativePath}\" {fileSize}\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            _Stream.Write(command, 0, command.Length);

            // Send the file data
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                fs.CopyTo(_Stream);
            }

            Console.WriteLine($"Sent file to Client: {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error Sent file to Client: {ex.Message}");
        }
    }
    private static void HandleFileRename(string oldFilePath, string newFilePath)
    {
        try
        {
            string relativeOldPath = Path.GetRelativePath(FolderPathToWatch, oldFilePath).Replace("\\", "/");
            string relativeNewPath = Path.GetRelativePath(FolderPathToWatch, newFilePath).Replace("\\", "/");
            string commandString = $"RENAME_FILE \"{relativeOldPath}\" \"{relativeNewPath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            _Stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent rename command from {relativeOldPath} to {relativeNewPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling file rename: {ex.Message}");
        }
    }

    private static void HandleFileDelete(string filePath)
    {
        try
        {
            string relativePath = Path.GetRelativePath(FolderPathToWatch, filePath).Replace("\\", "/");
            string commandString = $"DELETE_FILE \"{relativePath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            _Stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent delete command for file: {relativePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling file deletion: {ex.Message}");
        }
    }

    private static void HandleDirectoryCreated(string directoryPath)
    {
        try
        {
            string relativePath = Path.GetRelativePath(FolderPathToWatch, directoryPath).Replace("\\", "/");
            string commandString = $"CREATE_DIRECTORY \"{relativePath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            _Stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent create directory command: {relativePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating directory on Client: {ex.Message}");
        }
    }

    private static void HandleDirectoryRename(string oldPath, string newPath)
    {
        try
        {
            string relativeOldPath = Path.GetRelativePath(FolderPathToWatch, oldPath).Replace("\\", "/");
            string relativeNewPath = Path.GetRelativePath(FolderPathToWatch, newPath).Replace("\\", "/");
            string commandString = $"RENAME_DIRECTORY \"{relativeOldPath}\" \"{relativeNewPath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            _Stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent rename directory command from {relativeOldPath} to {relativeNewPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error renaming directory on Client: {ex.Message}");
        }
    }

    private static void HandleDirectoryDelete(string directoryPath)
    {
        try
        {
            string relativePath = Path.GetRelativePath(FolderPathToWatch, directoryPath).Replace("\\", "/");
            string commandString = $"DELETE_DIRECTORY \"{relativePath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            _Stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent delete directory command: {relativePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error deleting directory on Client: {ex.Message}");
        }
    }

}
