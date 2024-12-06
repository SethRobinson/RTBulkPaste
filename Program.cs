using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Text;
using System.Threading;

class Program
{
    const int MAX_FILE_SIZE = 500 * 1024; // 500 KB
    const int MAX_FILE_COUNT = 10000;

    static HashSet<string> visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static List<string> collectedFiles = new List<string>();
    static int fileCount = 0;
    static List<string> ignorePatterns = new List<string>();

    [STAThread]
    static void Main(string[] args)
    {
        if (args == null || args.Length == 0)
        {
            Console.WriteLine("RTBulkPaste V1.0 by Seth A. Robinson ( rtsoft.com )");
            Console.WriteLine("No files provided.");
            Console.WriteLine("Usage: RTBulkPaste.exe <fileOrDir1> <fileOrDir2> ...");
            Console.WriteLine("Copies contents of specified text files (or all in directories) to the clipboard.");
            DelayToView();
            return;
        }

        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
        var config = LoadConfig(configPath, out bool configFound);

        if (!configFound)
        {
            Console.WriteLine("Warning: config.txt not found. Using default pre/post messages.");
            config["prepaste_message"] = "(This is the contents of the file <FILEPATH>\\<FILENAME>)<CR><CR>";
            config["postpaste_message"] = "<CR><CR>(END OF <FILEPATH>\\<FILENAME>)<CR>";
        }

        string preMessage = config.ContainsKey("prepaste_message") ? config["prepaste_message"] : "";
        string postMessage = config.ContainsKey("postpaste_message") ? config["postpaste_message"] : "";

        // Load ignore patterns if available
        if (config.TryGetValue("ignore_patterns", out string ignoreValue))
        {
            // split by whitespace
            ignorePatterns = ignoreValue.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                        .ToList();
        }

        // Process each argument, which can be a file or directory
        foreach (var path in args)
        {
            ProcessPath(path);
            if (fileCount >= MAX_FILE_COUNT)
            {
                Console.WriteLine("File limit reached (10,000). Stopping further processing.");
                break;
            }
        }

        if (collectedFiles.Count == 0)
        {
            Console.WriteLine("None of the specified files were processed. No data was copied.");
            DelayToView();
            return;
        }

        var validContents = new List<string>();
        foreach (var filePath in collectedFiles)
        {
            if (!File.Exists(filePath))
            {
                // File disappeared or inaccessible.
                Console.WriteLine($"Skipping '{filePath}' - File does not exist or cannot be accessed.");
                continue;
            }

            var fi = new FileInfo(filePath);
            if (fi.Length > MAX_FILE_SIZE)
            {
                Console.WriteLine($"Skipping '{filePath}' - File too large (>500KB).");
                continue;
            }

            bool isBinary;
            try
            {
                isBinary = IsBinaryFile(filePath);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Skipping '{filePath}' - Could not determine if binary (locked?). Error: {ex.Message}");
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"Skipping '{filePath}' - Unauthorized access: {ex.Message}");
                continue;
            }

            if (isBinary)
            {
                Console.WriteLine($"Skipping '{filePath}' - Appears to be a binary file.");
                continue;
            }

            string fileContent;
            try
            {
                fileContent = ReadAllTextWithBOM(filePath);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Skipping '{filePath}' - File locked or unreadable: {ex.Message}");
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"Skipping '{filePath}' - Unauthorized access: {ex.Message}");
                continue;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Skipping '{filePath}' - Error reading file: {ex.Message}");
                continue;
            }

            string fileName = Path.GetFileName(filePath);
            string fileDir = Path.GetDirectoryName(filePath) ?? "";

            var processedPre = ReplaceTags(preMessage, fileName, fileDir);
            var processedPost = ReplaceTags(postMessage, fileName, fileDir);

            validContents.Add(processedPre + fileContent + processedPost);
            Console.WriteLine($"Added '{filePath}'");
        }

        if (validContents.Count == 0)
        {
            Console.WriteLine("No valid text files were processed. No data was copied.");
            DelayToView();
            return;
        }

        string combinedText = string.Join("\n", validContents);
        try
        {
            Clipboard.SetText(combinedText);
            Console.WriteLine("Clipboard updated successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to update clipboard: {ex.Message}");
        }

        DelayToView();
    }

    static void ProcessPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (Directory.Exists(path))
        {
            if (ShouldIgnore(Path.GetFileName(path)))
            {
                Console.WriteLine($"Ignoring directory '{path}' due to ignore patterns.");
                return;
            }

            var dirInfo = new DirectoryInfo(path).FullName;

            // Check for cycles or symlinks. If already visited, skip
            if (visitedDirectories.Contains(dirInfo))
            {
                Console.WriteLine($"Skipping '{path}' - already visited to prevent circular logic.");
                return;
            }

            visitedDirectories.Add(dirInfo);

            try
            {
                var entries = Directory.GetFileSystemEntries(path);
                foreach (var entry in entries)
                {
                    if (fileCount >= MAX_FILE_COUNT)
                    {
                        break;
                    }
                    ProcessPath(entry);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error accessing directory '{path}': {ex.Message}");
            }

            return;
        }

        // If it's a file
        if (File.Exists(path))
        {
            if (ShouldIgnore(Path.GetFileName(path)))
            {
                Console.WriteLine($"Ignoring file '{path}' due to ignore patterns.");
                return;
            }

            if (fileCount >= MAX_FILE_COUNT)
            {
                return;
            }

            collectedFiles.Add(path);
            fileCount++;
        }
        else
        {
            Console.WriteLine($"Skipping '{path}' - Not a file or directory.");
        }
    }

    static bool ShouldIgnore(string name)
    {
        foreach (var pattern in ignorePatterns)
        {
            if (WildcardMatch(name, pattern))
            {
                return true;
            }
        }
        return false;
    }

    // Simple wildcard match: '*' matches zero or more of any chars. '?' matches exactly one char.
    static bool WildcardMatch(string input, string pattern)
    {
        return IsMatch(input, pattern, 0, 0);

        bool IsMatch(string s, string p, int si, int pi)
        {
            if (pi == p.Length)
            {
                return si == s.Length;
            }

            if (p[pi] == '*')
            {
                for (int i = si; i <= s.Length; i++)
                {
                    if (IsMatch(s, p, i, pi + 1))
                    {
                        return true;
                    }
                }
                return false;
            }
            else if (p[pi] == '?')
            {
                return si < s.Length && IsMatch(s, p, si + 1, pi + 1);
            }
            else
            {
                return si < s.Length && s[si] == p[pi] && IsMatch(s, p, si + 1, pi + 1);
            }
        }
    }

    static void DelayToView()
    {
        // Delay 1 second so you can see the console
        Thread.Sleep(1000);
    }

    static Dictionary<string, string> LoadConfig(string path, out bool found)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        found = false;
        if (!File.Exists(path))
        {
            return dict;
        }

        found = true;
        var lines = File.ReadAllLines(path);
        foreach (var line in lines)
        {
            var parts = line.Split('|');
            if (parts.Length == 2)
            {
                dict[parts[0].Trim()] = parts[1].Trim();
            }
        }

        return dict;
    }

    static string ReplaceTags(string input, string fileName, string fileDir)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "";
        }

        input = input.Replace("<CR>", "\n");
        input = input.Replace("<FILENAME>", fileName);
        input = input.Replace("<FILEPATH>", fileDir);

        return input;
    }

    static bool IsBinaryFile(string path)
    {
        // Check if file contains a null char in first 512 bytes
        const int checkLength = 512;
        byte[] buffer = new byte[checkLength];

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            int read = fs.Read(buffer, 0, buffer.Length);
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    static string ReadAllTextWithBOM(string filePath)
    {
        // Attempt to read file even if locked by allowing sharing
        byte[] bytes;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytes = new byte[fs.Length];
            int bytesRead = fs.Read(bytes, 0, (int)fs.Length);
            if (bytesRead < fs.Length)
            {
                // Could not read fully; treat as unreadable
                throw new IOException("Could not fully read file.");
            }
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            // UTF-8 with BOM
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            // UTF-16 LE
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            // UTF-16 BE
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        // No BOM found, assume UTF-8
        return Encoding.UTF8.GetString(bytes);
    }
}
