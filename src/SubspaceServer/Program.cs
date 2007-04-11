using System;
using System.Collections.Generic;
using System.Windows.Forms;

using SS.Core;
using SS.Utilities;
using SS.Core.Packets;
using System.IO;

namespace SS
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            //Application.Run(new Form1());

            Server server = new Server(Environment.CurrentDirectory);
            server.Start();

            /*
            ModuleManager mm = new ModuleManager();
            mm.AddModule(new ArenaManager());
            mm.AddModule(new PlayerData());
            

            mm.LoadModules();

            PlayerData pd = mm.GetModule<PlayerData>();
            if (pd == null)
            {
                Console.WriteLine("unable to get module");
            }

            ArenaManager aman = mm.GetModule<ArenaManager>();
            if (aman == null)
            {
                Console.WriteLine("unable to get module");
            }
            */
            /*
            // timer test case
            Mainloop ml = new Mainloop();
            ml.SetTimer<int>(
                new TimerDelegate<int>(timerTestMethod),
                10000,
                1,
                123,
                1
            );

            System.Threading.Thread.Sleep(20000);

            ml.ClearTimer<int>(timerTestMethod, 1);
            */

            /*
            PData pdata = new PData();
            Console.WriteLine("Has Crown = " + pdata.HasCrown);
            pdata.HasCrown = true;
            Console.WriteLine("Has Crown = " + pdata.HasCrown);
            pdata.HasCrown = false;
            Console.WriteLine("Has Crown = " + pdata.HasCrown);

            pdata.NameManaged = "test";
            Console.WriteLine("Name: [" + pdata.NameManaged + "]");
            */

            /*
            byte[] arr = new byte[4];
            MemoryStream ms = new MemoryStream(arr);
            BinaryReader reader = new BinaryReader(ms, System.Text.Encoding.ASCII);
            arr[0] = 1;
            int value1 = reader.ReadInt32();
            ms.Position = 0;
            arr[0] = 2;
            int value2 = reader.ReadInt32();
            Console.WriteLine("value1 = " + value1);
            Console.WriteLine("value2 = " + value2);
            */

            /*
            ConfigManager cm = new ConfigManager();
            string strValue = cm.GetStr(cm.Global, "Listen", "Port");
            cm.CloseConfigFile(cm.Global);
            */

            /*
            // Config test cases
            ConfigHandle ch = ConfigFile.OpenConfigFile(null, null, null, null);
            string strValue = ConfigFile.GetStr(ch, "Listen", "Port");
            ConfigFile.CloseConfigFile(ch);

            ConfigHandle scf = ConfigFile.OpenConfigFile("scf", null, null, null);
            int intValue = ConfigFile.GetInt(scf, "Prize", "PrizeFactor", 0);
            ConfigFile.CloseConfigFile(scf);
            */

            /*
            // PathUtil test case
            string dest;
            string source = "arenas/%b/%n";
            Dictionary<char, string> repls = new Dictionary<char,string>(2);
            repls.Add('b', "(public)");
            repls.Add('n', "filename.cfg");
            if (PathUtil.macro_expand_string(out dest, source, repls, '%') == -1)
            {
                Console.WriteLine("error");
            }
            else
            {
                Console.WriteLine("success");
                Console.WriteLine(dest);
            }
            */

            /*
            string dst;
            string source = @"a\bcd\\efg=asdf";
            string leftover = Config.UnescapeString(out dst, source, '=');
            Console.WriteLine("source: [" + source + "]");
            Console.WriteLine("dest: [" + dst + "]");
            Console.WriteLine("leftover: [" + leftover + "]");
            */
            /*
            MultiDictionary<int, int> test = new MultiDictionary<int, int>();
            test.AddFirst(1, 123);
            test.AddFirst(2, 2222);
            test.AddFirst(1, 11);
            foreach(KeyValuePair<int, int> kvp in test)
            {
                Console.WriteLine(string.Format("Key[{0}] Value[{1}]", kvp.Key, kvp.Value));
            }
            Console.WriteLine("# Items: " + test.Count);

            test.Remove(1, 11);
            
            foreach (KeyValuePair<int, int> kvp in test)
            {
                Console.WriteLine(string.Format("Key[{0}] Value[{1}]", kvp.Key, kvp.Value));
            }
            Console.WriteLine("# Items: " + test.Count);
            */

            /*
            Dictionary<int, string> test = new Dictionary<int, string>();
            ICollection<KeyValuePair<int, string>> ic = test;
            //test.Add(new KeyValuePair<int, string>(1, "test"));
            //ic.Add(new KeyValuePair<int,string>(1, "test"));
            */

            /*
            // BufferPool test case
            BufferPool<SubspaceBuffer> pool = new BufferPool<SubspaceBuffer>();

            SubspaceBuffer buffer = pool.GetBuffer();
            buffer.Release();
            buffer.Release();

            Network network = new Network(pool);
            network.CreateSockets(7777, null);

            // TODO: create worker thread to listen for packets
            network.ListenForPackets();
            */
        }

        private static bool timerTestMethod(int arg)
        {
            Console.WriteLine("timer running: " + arg);
            System.Threading.Thread.Sleep(8000);
            Console.WriteLine("timer finished: " + arg);
            return true;
        }
    }
}