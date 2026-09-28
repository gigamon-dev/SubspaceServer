namespace SS.SourceGeneration.Tests
{
    public class ComponentCallbackGeneratorTests
    {
        [Fact]
        public Task NoParameters()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [ComponentCallback]
                public delegate void NoParametersCallback();
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }

        [Fact]
        public Task SingleIntParameter()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [ComponentCallback]
                public delegate void SingleIntParameterCallback(int x);
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }

        [Fact]
        public Task SingleArenaParameter()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [ComponentCallback]
                public delegate void SingleArenaParameterCallback(Arena arena);
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }

        [Fact]
        public Task SingleArenaParameterFullyQualified()
        {
            string source = """
                namespace Foo.Bar;

                [SS.Core.ComponentCallback]
                public delegate void SingleArenaParameterFullyQualifiedCallback(SS.Core.Arena arena);
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }

        [Fact]
        public Task SingleRefParameter()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [ComponentCallback]
                public delegate void SingleRefParameterCallback(ref int x);
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }

        [Fact]
        public Task SingleOutParameter()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [ComponentCallback]
                public delegate void SingleOutParameterCallback(out int x);
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }

        [Fact]
        public Task SingleInParameter()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [ComponentCallback]
                public delegate void SingleInParameterCallback(in int x);
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }

        [Fact]
        public Task SingleRefReadOnlyParameter()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [ComponentCallback]
                public delegate void SingleRefReadOnlyParameterCallback(ref readonly int x);
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }

        [Fact]
        public Task MultipleParameters()
        {
            string source = """
                using SS.Core;
                
                namespace Foo.Bar;

                [ComponentCallback]
                public delegate void MultipleParametersCallback(int x, string y, ref readonly double z);
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }

        [Fact]
        public Task BallPacketSent()
        {
            string source = """
                using SS.Core;
                using SS.Packets.Game;
                
                namespace Foo.Bar;

                /// <summary>
                /// Delegate for when a <see cref="S2CPacketType.Ball"/> packet is sent.
                /// </summary>
                /// <param name="arena">The arena.</param>
                /// <param name="ballPacket">The packet.</param>
                [ComponentCallback]
                public delegate void BallPacketSentCallback(Arena arena, ref readonly BallPacket ballPacket);
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }

        [Fact]
        public Task BallPacketSentFullyQualified()
        {
            string source = """
                namespace Foo.Bar;

                /// <summary>
                /// Delegate for when a <see cref="S2CPacketType.Ball"/> packet is sent.
                /// </summary>
                /// <param name="arena">The arena.</param>
                /// <param name="ballPacket">The packet.</param>
                [SS.Core.ComponentCallback]
                public delegate void BallPacketSentCallback(SS.Core.Arena arena, ref readonly SS.Packets.Game.BallPacket ballPacket);
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }

        [Fact]
        public Task PlayerAction()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;
                
                /// <summary>
                /// Delegate for a callback that is invoked when a <see cref="Player"/>'s life-cycle state changes.
                /// </summary>
                /// <param name="player">The player that changed state.</param>
                /// <param name="action">The new state.</param>
                /// <param name="arena">The <see cref="Arena"/> the player is in. <see langword="null"/> if the player is not in an <see cref="Arena"/>.</param>
                [ComponentCallback]
                public delegate void PlayerActionCallback(Player player, PlayerAction action, Arena? arena);
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }

        [Fact]
        public Task NestedType()
        {
            string source = """
                namespace Foo.Bar;

                [SS.Core.ComponentCallback]
                public delegate void NestedTypeCallback(MyContainingType.MyNestedType x);

                public static class MyContainingType
                {
                    public enum MyNestedType
                    {
                        Foo,
                        Bar,
                    }
                }
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }

        /// <summary>
        /// This test purposely should not output a result.
        /// </summary>
        [Fact]
        public Task GlobalNamespace()
        {
            string source = """
                [SS.Core.ComponentCallback]
                public delegate void GlobalNamespaceDelegate(int foo);
                """;

            return TestHelper.VerifyComponentCallbackGenerator(source);
        }
    }
}
