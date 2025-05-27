namespace SS.SourceGeneration.Tests
{
    public class CallbackHelperGeneratorTests
    {
        [Fact]
        public Task NoParameters()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [CallbackHelper]
                public static partial class NoParametersCallback
                {
                    public delegate void NoParametersDelegate();
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }

        [Fact]
        public Task SingleIntParameter()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [CallbackHelper]
                public static partial class SingleIntParameterCallback
                {
                    public delegate void SingleIntParameterDelegate(int x);
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }

        [Fact]
        public Task SingleArenaParameter()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [CallbackHelper]
                public static partial class SingleArenaParameterCallback
                {
                    public delegate void SingleArenaParameterDelegate(Arena arena);
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }

        [Fact]
        public Task SingleArenaParameterFullyQualified()
        {
            string source = """
                namespace Foo.Bar;

                [SS.Core.CallbackHelper]
                public static partial class SingleArenaParameterFullyQualifiedCallback
                {
                    public delegate void SingleArenaParameterFullyQualifiedDelegate(SS.Core.Arena arena);
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }

        [Fact]
        public Task SingleRefParameter()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [CallbackHelper]
                public static partial class SingleRefParameterCallback
                {
                    public delegate void SingleRefParameterDelegate(ref int x);
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }

        [Fact]
        public Task SingleOutParameter()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [CallbackHelper]
                public static partial class SingleOutParameterCallback
                {
                    public delegate void SingleOutParameterDelegate(out int x);
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }

        [Fact]
        public Task SingleInParameter()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [CallbackHelper]
                public static partial class SingleInParameterCallback
                {
                    public delegate void SingleInParameterDelegate(in int x);
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }

        [Fact]
        public Task SingleRefReadOnlyParameter()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;

                [CallbackHelper]
                public static partial class SingleRefReadOnlyParameterCallback
                {
                    public delegate void SingleRefReadOnlyParameterDelegate(ref readonly int x);
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }

        [Fact]
        public Task MultipleParameters()
        {
            string source = """
                using SS.Core;
                
                namespace Foo.Bar;

                [CallbackHelper]
                public static partial class MultipleParametersCallback
                {
                    public delegate void MultipleParametersDelegate(int x, string y, ref readonly double z);
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }

        [Fact]
        public Task BallPacketSent()
        {
            string source = """
                using SS.Core;
                using SS.Packets.Game;
                
                namespace Foo.Bar;

                [CallbackHelper]
                public static partial class BallPacketSentCallback
                {
                    /// <summary>
                    /// Delegate for when a <see cref="S2CPacketType.Ball"/> packet is sent.
                    /// </summary>
                    /// <param name="arena">The arena.</param>
                    /// <param name="ballPacket">The packet.</param>
                    public delegate void BallPacketSentDelegate(Arena arena, ref readonly BallPacket ballPacket);
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }

        [Fact]
        public Task BallPacketSentFullyQualified()
        {
            string source = """
                namespace Foo.Bar;

                [SS.Core.CallbackHelper]
                public static partial class BallPacketSentCallback
                {
                    /// <summary>
                    /// Delegate for when a <see cref="S2CPacketType.Ball"/> packet is sent.
                    /// </summary>
                    /// <param name="arena">The arena.</param>
                    /// <param name="ballPacket">The packet.</param>
                    public delegate void BallPacketSentDelegate(SS.Core.Arena arena, ref readonly SS.Packets.Game.BallPacket ballPacket);
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }

        [Fact]
        public Task PlayerAction()
        {
            string source = """
                using SS.Core;

                namespace Foo.Bar;
                
                [SS.Core.CallbackHelper]
                public static partial class PlayerActionCallback
                {
                    /// <summary>
                    /// Delegate for a callback that is invoked when a <see cref="Player"/>'s life-cycle state changes.
                    /// </summary>
                    /// <param name="player">The player that changed state.</param>
                    /// <param name="action">The new state.</param>
                    /// <param name="arena">The <see cref="Arena"/> the player is in. <see langword="null"/> if the player is not in an <see cref="Arena"/>.</param>
                    public delegate void PlayerActionDelegate(Player player, PlayerAction action, Arena? arena);
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }

        [Fact]
        public Task NestedType()
        {
            string source = """
                namespace Foo.Bar;

                [SS.Core.CallbackHelper]
                public static partial class NestedTypeCallback
                {
                    public enum MyNestedType
                    {
                        Foo,
                        Bar,
                    }

                    public delegate void NestedTypeDelegate(MyNestedType x);
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }

        /// <summary>
        /// This test purposely should not output a result.
        /// </summary>
        [Fact]
        public Task GlobalNamespace()
        {
            string source = """
                [SS.Core.CallbackHelper]
                public static partial class GlobalNamespaceCallback
                {
                    public delegate void GlobalNamespaceDelegate(int foo);
                }
                """;

            return TestHelper.VerifyCallbackHelperGenerator(source);
        }
    }
}
