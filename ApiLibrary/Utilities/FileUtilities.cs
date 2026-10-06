namespace ApiLibrary.Utilities
{
    public class FileUtilities
    {
        public bool IsFileLocked(string filePath)
        {
            try
            {
                using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    return false;
                }
            }
            catch (IOException)
            {
                return true;
            }
        }

        public void DeleteFile(string filePath, Action<string> logWarning, Action<string> logError, Action<string> logInfo)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);

                    logInfo($"File deleted successfully: {filePath}.\n");
                }
            }
            catch (Exception ex)
            {
                logError($"Error deleting file: {filePath} | {ex.Message}.\n\n\n");
            }
        }
    }
}
