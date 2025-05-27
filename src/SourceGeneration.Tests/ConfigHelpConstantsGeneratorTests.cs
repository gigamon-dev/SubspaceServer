namespace SS.SourceGeneration.Tests
{

    public class ConfigHelpConstantsGeneratorTests
    {
        [Fact]
        public Task BasicGeneration()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar
                {
                    [GenerateConfigHelpConstants(ConfigScope.Global, null)]
                    public static partial class Global
                    {
                    }

                    [GenerateConfigHelpConstantsAttribute(ConfigScope.Global, null)]
                    internal static partial class Baz
                    {
                    }

                    [GenerateConfigHelpConstantsAttribute(ConfigScope.Arena, null)]
                    public static partial class Arena
                    {
                    }

                    public class Test
                    {
                        [ConfigHelp<int>("Soccer", "NewGameDelay", ConfigScope.Arena, Default = -3000, 
                            Description = "How long to wait between games (in ticks). If this is negative, the actual delay is random, betwen zero and the absolute value.")]
                        public int Dummy;

                        [ConfigHelp("MySection", "MyKey", ConfigScope.Arena, DefaultValue = "MyDefaultValue", Description = "This is a non-generic ConfigHelp"]
                        public int Dummy2;
                    }
                }
                """;

            return TestHelper.VerifyConfigHelpConstantsGenerator(source);
        }

        [Fact]
        public Task LowerCaseSection()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar
                {
                    [GenerateConfigHelpConstants(ConfigScope.Global, null)]
                    public static partial class Global
                    {
                    }

                    [GenerateConfigHelpConstantsAttribute(ConfigScope.Global, null)]
                    public static partial class Baz
                    {
                    }

                    [GenerateConfigHelpConstantsAttribute(ConfigScope.Arena, null)]
                    public static partial class Arena
                    {
                    }

                    public class Test
                    {
                        [ConfigHelp("mysection", "MyKey", ConfigScope.Arena, DefaultValue = "MyDefaultValue", Description = "This is a non-generic ConfigHelp"]
                        public int Dummy;
                    }
                }
                """;

            return TestHelper.VerifyConfigHelpConstantsGenerator(source);
        }

        [Fact]
        public Task LowerCaseKey()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar
                {
                    [GenerateConfigHelpConstants(ConfigScope.Global, null)]
                    public static partial class Global
                    {
                    }

                    [GenerateConfigHelpConstantsAttribute(ConfigScope.Global, null)]
                    public static partial class Baz
                    {
                    }

                    [GenerateConfigHelpConstantsAttribute(ConfigScope.Arena, null)]
                    public static partial class Arena
                    {
                    }

                    public class Test
                    {
                        [ConfigHelp("MySection", "mykey", ConfigScope.Arena, DefaultValue = "MyDefaultValue", Description = "This is a non-generic ConfigHelp"]
                        public int Dummy;
                    }
                }
                """;

            return TestHelper.VerifyConfigHelpConstantsGenerator(source);
        }

        [Fact]
        public Task ConstructorWithLiteralFileName()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar
                {
                    [GenerateConfigHelpConstants(ConfigScope.Global, "test.conf")]
                    public static partial class TestGenerate
                    {
                    }

                    public class Test
                    {
                        [ConfigHelp<int>("Foo", "Bar", ConfigScope.Global, "test.conf", Default = 123, Description = "Testing")]
                        public int Dummy;
                    }
                }
                """;

            return TestHelper.VerifyConfigHelpConstantsGenerator(source);
        }

        [Fact]
        public Task ConstructorWithSymbolFileName()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar
                {
                    [GenerateConfigHelpConstants(ConfigScope.Global, "test.conf")]
                    public static partial class TestGenerate
                    {
                    }

                    public class Test
                    {
                        private const string TestFile = "test.conf";

                        [ConfigHelp<int>("Foo", "Bar", ConfigScope.Global, TestFile, Default = 123, Description = "Testing")]
                        public int Dummy;
                    }
                }
                """;

            return TestHelper.VerifyConfigHelpConstantsGenerator(source);
        }

        [Fact]
        public Task ConstructorWithIndirectSymbolFileName()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar
                {
                    [GenerateConfigHelpConstants(ConfigScope.Global, "test.conf")]
                    public static partial class TestGenerate
                    {
                    }

                    public class Test
                    {
                        private const string First = "test.conf";
                        private const string TestFile = First;

                        [ConfigHelp<int>("Foo", "Bar", ConfigScope.Global, FileName = TestFile, Default = 123, Description = "Testing")]
                        public int Dummy;
                    }
                }
                """;

            return TestHelper.VerifyConfigHelpConstantsGenerator(source);
        }

        [Fact]
        public Task ConstructorWithExternalSymbol()
        {
            string source = """
                using SS.Core;
                using System.Net;

                namespace Foo.Bar
                {
                    [GenerateConfigHelpConstants(ConfigScope.Global, null)]
                    public static partial class TestGenerate
                    {
                    }

                    public class Test
                    {
                        [ConfigHelp<int>("Foo", "Bar", ConfigScope.Global, Default = IPEndPoint.MaxPort, Description = "Testing")]
                        public int Dummy;
                    }
                }
                """;

            return TestHelper.VerifyConfigHelpConstantsGenerator(source);
        }
    }
}