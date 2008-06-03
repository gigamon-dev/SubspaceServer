using System;
using System.Collections.Generic;
using System.Windows.Forms;

using SS.Core;
using SS.Utilities;
using SS.Core.Packets;
using System.IO;
using System.Reflection;

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
            
            Application.Run(new Form1());

            /*
            uint ui = uint.MaxValue;
            Console.WriteLine("0x{0:X},", ui);
            byte b = (byte)ui;
            Console.WriteLine("0x{0:X},", b);
            */

            /*
            // sign extension masks
            for (int x = 0; x <= 31; x++)
            {
                uint val = 0xFFFFFFFF;
                Console.WriteLine("0x{1:X},", x, val << x);
            }
            */

            // 
            for (int x = 0; x <= 32; x++)
            {
                uint val = 0xFFFFFFFF;
                Console.WriteLine("0x{0:X},", ~(val << x));
            }

            /*
            // right shift on a signed integer maintains the sign bit (divide by 2)
            sbyte x = sbyte.MinValue;
            x >>= 1;
            x >>= 1;
            */

            //byte[] arr = { 0x00, 0x01, 0xFE };
            //byte b = ExtendedBitConverter.ToByte(arr, 1, 7, 8);
            //sbyte sb = ExtendedBitConverter.ToSByte(arr, 1, 7, 8);

            //b = ExtendedBitConverter.ToByte(arr, 1, 7, 7);
            //sb = ExtendedBitConverter.ToSByte(arr, 1, 7, 7);
            //BitConverter.ToInt32(null, 0);

            //byte[] testData = new byte[512];
            //SimplePacketTest p = new SimplePacketTest(testData);
            //byte type = p.Type;

            //uint t = 0xFFFFFFFF;
            //ushort s1 = 0xFFFF;
            //ushort s2 = 0x11;

            //object t = s1 & s2;

            /*
            // and operation on two bytes results in an int32
            byte b1 = 0xFF;
            byte b2 = 0x01;
            object t = b1 & b2;
            */

            byte b1 = 0xFF;
            uint ui = 123;
            object o = b1 & ui;

            //uint u1 = 0x01;
            //object t = u1 & b1;
            
            
            //uint b3 = (uint)b1 & b2;
            //uint val = 0xFFFFFFFF;
            //int test = (int)val;
            //uint test2 = (uint)test;
            //uint test3 = val & test2;
            //uint test4 = val & test;

            //uint val2 = 

            //int val = 0x1234abcd;
            //byte[] arr = BitConverter.GetBytes(val);
            //int x = ExtendedBitConverter.ToInt32(arr, 0, 0);
            //Console.WriteLine(x);

            /*
            Server server = new Server(Environment.CurrentDirectory);
            server.Start();
            System.Threading.Thread.Sleep(5000);
            server.Stop();
            */

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