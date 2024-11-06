using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Timers;

class FileWatcher
{
    public TcpClient Client { get; set; }
    public NetworkStream Stream { get; set; }
    public string FolderPathToWatch { get; set; }
    public Dictionary<string, bool> PathIsDirectory { get; set; } = new Dictionary<string, bool>();
    public object PathIsDirectoryLock { get; set; } = new object();
    public FileSystemWatcher watcher { get; set; }

    private readonly ConcurrentDictionary<string, DateTime> ChangeEvents = new ConcurrentDictionary<string, DateTime>();
    private readonly System.Timers.Timer DebounceTimer = new System.Timers.Timer(500); // 500ms debounce interval
    private readonly object DebounceLock = new object();

    public void StartFileWatcher()
    {
        InitializePathCache();

        watcher = new FileSystemWatcher
        {
            Path = FolderPathToWatch,
            IncludeSubdirectories = true, // Monitor subdirectories
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024 // Increase buffer size to 64 KB
        };
        // Subscribe to events
        watcher.Changed += (s, e) => OnChanged(s, e);
        watcher.Created += (s, e) => OnCreated(s, e);
        watcher.Deleted += (s, e) => OnDeleted(s, e);
        watcher.Renamed += (s, e) => OnRenamed(s, e);

        // Start watching
        watcher.EnableRaisingEvents = true;

        DebounceTimer.Elapsed += OnDebounceTimerElapsed;
        DebounceTimer.AutoReset = false;

        Console.WriteLine($"Server is watching for changes in {FolderPathToWatch}...");

    }
    private void InitializePathCache()
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
    private void OnCreated(object sender, FileSystemEventArgs e)
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
    private void HandleFileSystemEvent(FileSystemEventArgs e)
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

    private void OnDebounceTimerElapsed(object sender, ElapsedEventArgs e)
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
    private void OnChanged(object sender, FileSystemEventArgs e)
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

    private void OnDeleted(object sender, FileSystemEventArgs e)
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

    private void OnRenamed(object sender, RenamedEventArgs e)
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
    private void HandleFileChange(string filePath)
    {
        try
        {
            //WaitForFileAccess(filePath);
            Thread.Sleep(1000);
            // Get the relative file path
            string relativePath = Path.GetRelativePath(FolderPathToWatch, filePath).Replace("\\", "/");

            // Get the file size
            FileInfo fileInfo = new FileInfo(filePath);
            long fileSize = fileInfo.Length;

            // Construct the command
            string commandString = $"CHANGE_FILE \"{relativePath}\" {fileSize}\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            Stream.Write(command, 0, command.Length);

            // Send the file data
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                fs.CopyTo(Stream);
            }

            Console.WriteLine($"Sent file to Client: {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error Sent file to Client: {ex.Message}");
        }
    }    
    private void HandleFileRename(string oldFilePath, string newFilePath)
    {
        try
        {
            string relativeOldPath = Path.GetRelativePath(FolderPathToWatch, oldFilePath).Replace("\\", "/");
            string relativeNewPath = Path.GetRelativePath(FolderPathToWatch, newFilePath).Replace("\\", "/");
            string commandString = $"RENAME_FILE \"{relativeOldPath}\" \"{relativeNewPath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            Stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent rename command from {relativeOldPath} to {relativeNewPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling file rename: {ex.Message}");
        }
    }

    private void HandleFileDelete(string filePath)
    {
        try
        {
            string relativePath = Path.GetRelativePath(FolderPathToWatch, filePath).Replace("\\", "/");
            string commandString = $"DELETE_FILE \"{relativePath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            Stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent delete command for file: {relativePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling file deletion: {ex.Message}");
        }
    }

    private void HandleDirectoryCreated(string directoryPath)
    {
        try
        {
            string relativePath = Path.GetRelativePath(FolderPathToWatch, directoryPath).Replace("\\", "/");
            string commandString = $"CREATE_DIRECTORY \"{relativePath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            Stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent create directory command: {relativePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating directory on Client: {ex.Message}");
        }
    }

    private void HandleDirectoryRename(string oldPath, string newPath)
    {
        try
        {
            string relativeOldPath = Path.GetRelativePath(FolderPathToWatch, oldPath).Replace("\\", "/");
            string relativeNewPath = Path.GetRelativePath(FolderPathToWatch, newPath).Replace("\\", "/");
            string commandString = $"RENAME_DIRECTORY \"{relativeOldPath}\" \"{relativeNewPath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            Stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent rename directory command from {relativeOldPath} to {relativeNewPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error renaming directory on Client: {ex.Message}");
        }
    }

    private void HandleDirectoryDelete(string directoryPath)
    {
        try
        {
            string relativePath = Path.GetRelativePath(FolderPathToWatch, directoryPath).Replace("\\", "/");
            string commandString = $"DELETE_DIRECTORY \"{relativePath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            Stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent delete directory command: {relativePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error deleting directory on Client: {ex.Message}");
        }
    }

}

class ListenMessageforClient
{
    FileWatcher clientInfo;
    public ListenMessageforClient(FileWatcher _clientInfo)
    {
        clientInfo = _clientInfo;
    }
    public void ListenHandleMessage()
    {
        try
        {
            NetworkStream stream = clientInfo.Stream;
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
                    HandleChangeFile(commandArgs, stream, clientInfo);
                    command = "";
                }
                else if (command.StartsWith("RENAME_FILE "))
                {
                    Console.WriteLine($"Recieved Message {command}");
                    string commandArgs = command.Substring("RENAME_FILE ".Length).Trim();
                    HandleRenameFile(commandArgs, clientInfo);
                    command = "";
                }
                else if (command.StartsWith("DELETE_FILE "))
                {
                    Console.WriteLine($"Recieved Message {command}");
                    string commandArgs = command.Substring("DELETE_FILE ".Length).Trim();
                    HandleDeleteFile(commandArgs, clientInfo);
                    command = "";
                   
                }
                else if (command.StartsWith("CREATE_DIRECTORY "))
                {
                    Console.WriteLine($"Recieved Message {command}");
                    string commandArgs = command.Substring("CREATE_DIRECTORY ".Length).Trim();
                    HandleCreateDirectory(commandArgs, clientInfo);
                    command = "";
                }
                else if (command.StartsWith("RENAME_DIRECTORY "))
                {
                    Console.WriteLine($"Recieved Message {command}");
                    string commandArgs = command.Substring("RENAME_DIRECTORY ".Length).Trim();
                    HandleRenameDirectory(commandArgs, clientInfo);
                    command = "";
                }
                else if (command.StartsWith("DELETE_DIRECTORY "))
                {
                    Console.WriteLine($"Recieved Message {command}");
                    string commandArgs = command.Substring("DELETE_DIRECTORY ".Length).Trim();
                    HandleDeleteDirectory(commandArgs, clientInfo);
                    command = "";
                }
                else
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid command received: {command}");
                    command = "";
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error handling client: {ex.Message}");
        }
        finally
        {
            try
            {
                clientInfo.Stream.Close();
                clientInfo.Client.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error closing client connection: {ex.Message}");
            }
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Client disconnected.");
        }
    }
    private void HandleChangeFile(string commandArgs, NetworkStream stream, FileWatcher clientInfo)
    {
        // Regular expression to match a quoted string and file size
        Regex regex = new Regex("^\"([^\"]+)\"\\s+(\\d+)$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            clientInfo.watcher.EnableRaisingEvents = false;
            string fileName = match.Groups[1].Value;
            string fileSizeStr = match.Groups[2].Value;

            if (!long.TryParse(fileSizeStr, out long fileSize))
            {
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid file size.");
                return;
            }

            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Receiving file: {fileName}, size: {fileSize}");

            // Specify the path to save the uploaded file
            string savePath = Path.Combine(clientInfo.FolderPathToWatch, fileName);
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
                        Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Warning: Expected file size does not match the received data.");
                    }
                }

                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File received and saved as: {savePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error Saved file: {ex.Message}");
            }
            finally
            {
                clientInfo.watcher.EnableRaisingEvents = true;
            }
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid UPLOAD_FILE command format.");
        }
    }
    private void HandleRenameFile(string commandArgs, FileWatcher clientInfo)
    {
        // Regular expression to match two quoted strings
        Regex regex = new Regex("^\"([^\"]+)\"\\s+\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string oldFileName = match.Groups[1].Value;
            string newFileName = match.Groups[2].Value;

            string oldFilePath = Path.Combine(clientInfo.FolderPathToWatch, oldFileName);
            string newFilePath = Path.Combine(clientInfo.FolderPathToWatch, newFileName);

            clientInfo.watcher.EnableRaisingEvents = false;
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
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File renamed from {oldFileName} to {newFileName}");
                }
                else
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File not found: {oldFileName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error renaming file: {ex.Message}");
            }
            finally
            {
                clientInfo.watcher.EnableRaisingEvents = true;
            }
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid RENAME_FILE command format.");
        }
    }

    private void HandleDeleteFile(string commandArgs, FileWatcher clientInfo)
    {
        // Regular expression to match a quoted string
        Regex regex = new Regex("^\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string fileName = match.Groups[1].Value;
            string filePath = Path.Combine(clientInfo.FolderPathToWatch, fileName);

            clientInfo.watcher.EnableRaisingEvents = false;
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File deleted: {fileName}");
                }
                else
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File not found: {fileName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error deleting file: {ex.Message}");
            }
            finally
            {
                clientInfo.watcher.EnableRaisingEvents = true;
            }
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid DELETE_FILE command format.");
        }
    }

    private void HandleCreateDirectory(string commandArgs, FileWatcher clientInfo)
    {
        // Regular expression to match a quoted directory path
        Regex regex = new Regex("^\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string relativePath = match.Groups[1].Value;
            string directoryPath = Path.Combine(clientInfo.FolderPathToWatch, relativePath);

            clientInfo.watcher.EnableRaisingEvents = false;
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory created: {directoryPath}");
                }
                else
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory already exists: {directoryPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error creating directory: {ex.Message}");
            }
            finally
            {
                clientInfo.watcher.EnableRaisingEvents = true;
            }
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid CREATE_DIRECTORY command format.");
        }
    }

    private void HandleRenameDirectory(string commandArgs, FileWatcher clientInfo)
    {
        // Regular expression to match two quoted directory paths
        Regex regex = new Regex("^\"([^\"]+)\"\\s+\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string oldRelativePath = match.Groups[1].Value;
            string newRelativePath = match.Groups[2].Value;
            string oldDirectoryPath = Path.Combine(clientInfo.FolderPathToWatch, oldRelativePath);
            string newDirectoryPath = Path.Combine(clientInfo.FolderPathToWatch, newRelativePath);

            clientInfo.watcher.EnableRaisingEvents = false;
            try
            {
                if (Directory.Exists(oldDirectoryPath))
                {
                    Directory.Move(oldDirectoryPath, newDirectoryPath);
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory renamed from {oldRelativePath} to {newRelativePath}");
                }
                else
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory not found: {oldRelativePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error renaming directory: {ex.Message}");
            }
            finally
            {
                clientInfo.watcher.EnableRaisingEvents = true;
            }
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid RENAME_DIRECTORY command format.");
        }
    }

    private void HandleDeleteDirectory(string commandArgs, FileWatcher clientInfo)
    {
        // Regular expression to match a quoted directory path
        Regex regex = new Regex("^\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string relativePath = match.Groups[1].Value;
            string directoryPath = Path.Combine(clientInfo.FolderPathToWatch, relativePath);

            clientInfo.watcher.EnableRaisingEvents = false;
            try
            {
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, true);
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory deleted: {directoryPath}");
                }
                else
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory not found: {directoryPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error deleting directory: {ex.Message}");
            }
            finally
            {
                clientInfo.watcher.EnableRaisingEvents = true;
            }
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid DELETE_DIRECTORY command format.");
        }
    }
    private string ReadLineFromStream(NetworkStream stream)
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

}
class SyncFileServer
{
    private static TcpListener _listener;  
    static void Main()
    {
        _listener = new TcpListener(IPAddress.Any, 5000);
        _listener.Start();
        Console.WriteLine("Server started. Waiting for connections...");
        while (true)
        {
            TcpClient client = _listener.AcceptTcpClient();
            Thread clientThread = new Thread(() =>
            {
                IPEndPoint clientEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                if (clientEndPoint != null)
                {
                    string clientIp = clientEndPoint.Address.ToString().Replace(":", "_"); // Replace ':' to avoid path issues
                    int clientPort = clientEndPoint.Port;
                    string clientFolder = Path.Combine(@"C:\Server", $"{clientIp}");

                    if (!Directory.Exists(clientFolder))
                    {
                        Directory.CreateDirectory(clientFolder);
                    }

                    FileWatcher clientInfo = new FileWatcher
                    {
                        Client = client,
                        Stream = client.GetStream(),
                        FolderPathToWatch = clientFolder,
                    };
                    // Initialize FileSystemWatcher
                    clientInfo.StartFileWatcher();

                    Console.WriteLine($"Client connected from IP: {clientEndPoint.Address}, Port: {clientPort}");
                    ListenMessageforClient listenMessageforClient = new ListenMessageforClient(clientInfo);
                    listenMessageforClient.ListenHandleMessage();
                }
                else
                {
                    Console.WriteLine("Client connected. Could not retrieve IP address.");
                }
            });
            clientThread.Start(); // Start handling the client on a new thread
        }        
    }    
}