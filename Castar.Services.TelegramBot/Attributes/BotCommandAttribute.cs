using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Services.TelegramBot.Attributes
{
    [AttributeUsage(AttributeTargets.Method)]
    internal class BotCommandAttribute:Attribute
    {
        public string command { get; set; }
        public string description { get; set; }
        public BotCommandAttribute(string command,string description)
        {
            this.command = command;
            this.description = description;
        }
    }
}
