using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Castar.Logger
{

    public sealed class CLogger : ICLogger
    {
        private readonly string filePath;
        public CLogger(string filePath)
        {
            this.filePath = filePath;
        }
        private async Task StartTask(string content, string level, string className)
        {
            var custom = CustomizeContent(content, level, className);
            CreateDirectory();
            await WriteToFile(custom);
        }
        private void CreateDirectory()
        {
            var directoryName = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(directoryName))
                Directory.CreateDirectory(directoryName);
        }
        private async Task WriteToFile(string content) =>
            await File.AppendAllTextAsync(filePath, content);
        private string CustomizeContent(string content, string level, string className)
        {
            var dt = DateTime.Now.ToString("MM.dd.yyyy HH:mm:ss:FFF"); ;
            return ($"[{dt}] <{className}> {level}: {content}\n");
        }
        public async void Log<T>(string message, TypeLog type) where T : class =>
            await StartTask($"{message}", type.ToString(), typeof(T).Name);
    }
}