using System;
using System.Collections.Generic;

namespace codeLess.Core
{
    public class CommandDefinition
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public string DefaultKeyMac { get; set; }
        public string DefaultKeyWin { get; set; }
        public string DefaultKeyLinux { get; set; }
        public string When { get; set; }

        public CommandDefinition(string id, string title, string category, string defaultKeyMac, string defaultKeyWin, string defaultKeyLinux, string when)
        {
            this.Id = id;
            this.Title = title;
            this.Category = category;
            this.DefaultKeyMac = defaultKeyMac;
            this.DefaultKeyWin = defaultKeyWin;
            this.DefaultKeyLinux = defaultKeyLinux;
            this.When = when;
        }
    }

    public static class CommandRegistry
    {
        private static readonly Dictionary<string, CommandDefinition> commands = new Dictionary<string, CommandDefinition>();

        public static void RegisterCommand(CommandDefinition def)
        {
            commands[def.Id] = def;
        }

        public static IEnumerable<CommandDefinition> AllCommands
        {
            get { return commands.Values; }
        }

        public static CommandDefinition GetCommand(string id)
        {
            CommandDefinition def;
            commands.TryGetValue(id, out def);
            return def;
        }
    }
}
