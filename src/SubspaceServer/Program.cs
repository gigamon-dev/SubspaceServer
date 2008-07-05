using System;
using System.Collections.Generic;
using System.Windows.Forms;

using SS.Core;
using SS.Utilities;
using SS.Core.Packets;
using System.IO;
using System.Reflection;
using System.Text;
using System.Collections.Specialized;

namespace SS
{
    static class Program
    {
        private class TestClass
        {
            public TestStruct ts;
        }

        private struct TestStruct
        {
            public byte[] ByteArray;
            public int IntValue;

            public TestStruct(byte[] d)
            {
                ByteArray = d;
                IntValue = 5;
            }
        }

        private struct TestStruct1
        {
            public int x;
        }

        private struct TestStruct2
        {
            public int x;
            public static int y;
        }

        private static int tickDiff(uint a, uint b)
        {
            int retVal = ((int)(((a) << 1) - ((b) << 1)) >> 1);
            return retVal;
        }

        private static bool tickGt(uint a, uint b)
        {
            int diff = tickDiff(a, b);
            return tickDiff(a, b) > 0;
        }

        public delegate void TestDelegate(int x, ref int y);

        public static void MyMethodA(int x, ref int y)
        {
            y++;
        }

        public static void MyMethodB(int x, ref int y)
        {
            y++;
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            
            Application.Run(new Form1());

            //uint test = uint.MaxValue;
            //byte val = ExtendedBitConverter.GetByte(test, 24, 8);
            //test = ExtendedBitConverter.SetByte(test, 0, 24, 7);
            //byte val2 = ExtendedBitConverter.GetByte(test, 24, 8);
            
            //int x=1, y=2;
            //TestDelegate d = new TestDelegate(MyMethodA);
            //d += new TestDelegate(MyMethodB);
            //IntPtr ip = ;
            
            //d(x, ref y);
            //d.DynamicInvoke(x, new IntPtr(y));
            //Console.WriteLine("x=" + x);
            //Console.WriteLine("y=" + y);
            
            /*
            ServerTick t1 = ServerTick.Now;
            System.Threading.Thread.Sleep(1000);
            ServerTick t2 = ServerTick.Now;

            int diff = t2 - t1;
            int diff2 = t1 - t2;

            bool gt = t2 > t1;
            bool gt2 = t1 > t2;

            bool lt = t2 < t1;
            bool lt2 = t1 < t2;
            */

            /*
            uint a = 0x7FFFFFFF;
            uint b = a+2;

            int diff = tickDiff(a, b);
            int diff2 = tickDiff(b,a);

            bool gt1 = tickGt(a, b);
            bool gt2 = tickGt(b, a);
            */

            /*
            string hexString = @"0f 00 00 00 70 17 00 00
a0 0f 00 00 dc 05 64 00 14 00 1e 00 2c 01 32 00
0e 01 96 00 d0 07 d0 07 01 00 f4 01 64 00 4d 01
64 00 fa 00 e6 00 11 00 b2 0c 7e 04 a4 06 c8 00
0f 00 da 07 90 01 e8 03 28 00 02 00 fa 00 a6 00
64 00 b0 04 90 01 b8 0b 01 00 7d 00 19 00 32 00
96 00 7d 00 e8 03 f4 01 1e 00 64 00 00 00 b0 04
0c 00 40 00 c4 09 05 18 05 03 03 03 03 03 03 03
00 00 00 00 00 00 00 00 48 10 31 02 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 70 17 00 00
a0 0f 00 00 dc 05 64 00 14 00 1e 00 2c 01 32 00
0e 01 96 00 d0 07 d0 07 00 00 f4 01 64 00 4d 01
64 00 fa 00 e6 00 11 00 b2 0c 7e 04 a4 06 c8 00
0f 00 da 07 90 01 e8 03 28 00 02 00 fa 00 a6 00
64 00 b0 04 90 01 b8 0b 01 00 7d 00 19 00 32 00
96 00 7d 00 e8 03 f4 01 1e 00 64 00 00 00 b0 04
0c 00 40 00 c4 09 05 18 05 03 03 03 03 03 03 03
00 00 00 00 00 00 00 00 48 50 30 02 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 70 17 00 00
a0 0f 00 00 dc 05 64 00 14 00 1e 00 2c 01 32 00
0e 01 96 00 d0 07 d0 07 00 00 f4 01 64 00 4d 01
64 00 fa 00 e6 00 11 00 b2 0c 7e 04 a4 06 c8 00
0f 00 da 07 90 01 e8 03 28 00 02 00 fa 00 a6 00
64 00 b0 04 90 01 b8 0b 01 00 7d 00 19 00 32 00
96 00 7d 00 e8 03 f4 01 1e 00 64 00 00 00 b0 04
0c 00 40 00 c4 09 05 18 05 03 03 03 03 03 03 03
00 00 00 00 00 00 00 00 48 58 31 02 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 70 17 00 00
a0 0f 00 00 dc 05 64 00 14 00 1e 00 2c 01 32 00
0e 01 96 00 d0 07 d0 07 00 00 f4 01 64 00 4d 01
64 00 fa 00 e6 00 11 00

b2 0c 7e 04 a4 06 c8 00
0f 00 da 07 90 01 e8 03 28 00 02 00 fa 00 a6 00
64 00 b0 04 90 01 b8 0b 01 00 7d 00 19 00 32 00
96 00 7d 00 e8 03 f4 01 1e 00 64 00 00 00 b0 04
0c 00 40 00 c4 09 05 18 05 03 03 03 03 03 03 03
00 00 00 00 00 00 00 00 48 40 31 03 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 70 17 00 00
a0 0f 00 00 dc 05 64 00 14 00 1e 00 2c 01 32 00
0e 01 96 00 d0 07 d0 07 00 00 f4 01 64 00 4d 01
64 00 fa 00 e6 00 11 00 b2 0c 7e 04 a4 06 c8 00
0f 00 da 07 90 01 e8 03 28 00 02 00 fa 00 a6 00
64 00 b0 04 90 01 b8 0b 01 00 7d 00 19 00 32 00
96 00 7d 00 e8 03 f4 01 1e 00 64 00 00 00 b0 04
0c 00 40 00 c4 09 05 18 05 03 03 03 03 03 03 03
00 00 00 00 00 00 00 00 48 50 31 06 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 70 17 00 00
a0 0f 00 00 dc 05 64 00 14 00 1e 00 2c 01 32 00
0e 01 96 00 d0 07 d0 07 00 00 f4 01 64 00 4d 01
64 00 fa 00 e6 00 11 00 b2 0c 7e 04 a4 06 c8 00
0f 00 da 07 90 01 e8 03 28 00 02 00 fa 00 a6 00
64 00 b0 04 90 01 b8 0b 01 00 7d 00 19 00 32 00
96 00 7d 00 e8 03 f4 01 1e 00 64 00 00 00 b0 04
0c 00 40 00 c4 09 05 18 05 03 03 03 03 03 03 03
00 00 00 00 00 00 00 00 48 50 31 1a 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 70 17 00 00
a0 0f 00 00 dc 05 64 00 14 00 1e 00 2c 01 32 00
0e 01 96 00 d0 07 d0 07 00 00 f4 01 64 00 4d 01
64 00 fa 00 e6 00 11 00 b2 0c 7e 04 a4 06 c8 00
0f 00 da 07 90 01 e8 03 28 00 02 00 fa 00 a6 00
64 00 b0 04 90 01 b8 0b 01 00 7d 00 19 00 32 00
96 00 7d 00 e8 03 f4 01

1e 00 64 00 00 00 b0 04
0c 00 40 00 c4 09 05 18 05 03 03 03 03 64 03 03
00 00 64 00 00 00 00 01 48 50 31 02 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 70 17 00 00
a0 0f 00 00 00 00 64 00 14 00 1e 00 2c 01 32 00
0e 01 96 00 d0 07 d0 07 00 00 f4 01 64 00 4d 01
64 00 fa 00 e6 00 11 00 b2 0c 7e 04 a4 06 c8 00
0f 00 da 07 90 01 e8 03 28 00 02 00 fa 00 a6 00
64 00 b0 04 90 01 b8 0b 01 00 7d 00 19 00 32 00
96 00 7d 00 e8 03 f4 01 1e 00 64 00 00 00 b0 04
0c 00 40 00 c4 09 05 18 05 03 03 03 03 03 03 03
00 00 00 00 00 00 00 00 5f 54 31 02 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 40 0d 03 00
e0 70 72 00 26 02 00 00 40 1f 00 00 dc 05 00 00
90 5f 01 00 84 03 00 00 0f 27 00 00 88 13 00 00
e0 2e 00 00 60 ae 0a 00 a0 86 01 00 b8 0b 00 00
e8 03 00 00 19 00 00 00 7c 15 00 00 b8 0b 00 00
a0 86 01 00 00 00 00 00 b8 0b 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 f4 01 96 00
14 00 50 00 20 03 48 00 c8 00 bc 02 03 00 06 00
16 00 0a 00 00 00 00 00 00 00 e1 00 00 02 e8 03
01 00 02 00 10 27 bc 02 0a 00 06 00 40 1f a0 0f
2c 01 58 02 e8 03 fe ff c8 00 f4 01 0a 00 e8 03
14 00 90 01 e8 03 80 00 70 17 00 00 e8 03 e8 03
32 00 00 00 e8 03 e8 03 00 00 14 00 c8 00 f4 01
00 00 00 00 64 00 00 00 00 00 00 00 00 00 00 00
01 01 01 01 00 06 00 0c 01 01 00 00 01 0a 00 00
01 00 00 00 01 01 01 01 00 00 00 00 00 00 00 00
28 28 28 28 19 28 07 19 19 19 28 28 0a 00 1e 19
0a 0a 1e 19 46 46 28 1e 82 c8 32 3c";
            //string[] arr = hexString.Split(new char[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            hexString = hexString.Replace(" ", ",0x").Replace(Environment.NewLine, ",0x");
            Console.WriteLine(hexString);
             */
             
            //Console.WriteLine("ClientSettingsPacke.Length = {0}" + ClientSettingsPacket.Length);
            /*
            DataBuffer buffer = new DataBuffer();
            C2SPositionPacket pos = new C2SPositionPacket(buffer.Bytes);
            Weapons w = pos.Weapon;
            Weapons src = new Weapons(new byte[512], 0);
            src.Type = 1;
            src.Level = 2;
            src.ShrapBouncing = true;
            src.ShrapLevel = 2;
            src.Shrap = 32;
            src.Alternate = true;

            pos.Weapon = src;
            */
            //pos.Weapon.Type

            /*
            ClientSettingsPacket s = new ClientSettingsPacket();
            ClientSettingsPacket.ClientBitSet sc = s.BitSet;
            sc.HideFlags = 1;
            s.BitSet.HideFlags = 1;
            s.LongSet[0] = 123;
            //s.TestBitSet.HideFlags = 1;
            //s.TestBitSet2.HideFlags = 1;
            */

            /*
            unsafe
            {
                Console.WriteLine("TestStruct1: {0}", sizeof(TestStruct1));
                Console.WriteLine("TestStruct2: {0}", sizeof(TestStruct2));
            }
            */

            /*
            byte[] test = new byte[5];
            test[0] = (byte)'a';

            string s1 = Encoding.ASCII.GetString(test);
            string s2 = Encoding.ASCII.GetString(test, 0, 2);
            string s3 = s1.Trim('\0');
            */

            /*
            // value type test
            TestClass tc = new TestClass();
            TestStruct ts = tc.ts = new TestStruct(new byte[] { 1, 2, 3 });
            ts.ByteArray[0] = 4;
            tc.ts.ByteArray[1] = 131;
            ts.IntValue = 123;
            */

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

            /*
            for (int x = 0; x <= 32; x++)
            {
                uint val = 0xFFFFFFFF;
                Console.WriteLine("0x{0:X},", ~(val << x));
            }
            */

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

            /*
            byte b1 = 0xFF;
            uint ui = 123;
            object o = b1 & ui;
            */

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