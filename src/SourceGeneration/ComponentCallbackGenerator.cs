using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

namespace SS.SourceGeneration
{
    /// <summary>
    /// Generates methods for callback delegates that are decorated with the SS.Core.ComponentCallbackAttribute.
    /// </summary>
    /// <remarks>
    /// Register, Unregister, and Fire methods are generated.
    /// <para>
    /// The logic for invoking a callback in the Fire method invokes the callback for all registered handlers on the specified broker and all of its parent brokers, recursively.
    /// It also handles catching exceptions so that an exception thrown by one handler will not prevent execution of other handlers.
    /// Also, caught exceptions are logged to the ILogManager, or if that does not exist, Console.Error.
    /// </para>
    /// <para>
    /// For example, the following code:
    /// <code>
    /// [ComponentCallback]
    /// public delegate void FooDelegate(int x, string y, ref readonly MyLargeStruct z);
    /// </code>
    /// Will generate static extension methods that look like:
    /// <code>
    /// public static class FooDelegateExtensions
    /// {
    ///     extension(FooDelegate)
    ///     {
    ///         public static void Register(IComponentBroker broker, FooDelegate handler)
    ///         {
    ///             // logic to register
    ///         }
    /// 
    ///         public static void Unregister(IComponentBroker broker, FooDelegate handler)
    ///         {
    ///             // logic to unregister
    ///         }
    /// 
    ///         public static void Fire(IComponentBroker broker, int x, string y, ref readonly MyLargeStruct z)
    ///         {
    ///             // logic to invoke
    ///         }
    ///     }
    /// }
    /// </code>
    /// </para>
    /// </remarks>
    [Generator(LanguageNames.CSharp)]
    public class ComponentCallbackGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var generateClassTargetProvider = context.SyntaxProvider.ForAttributeWithMetadataName(
                "SS.Core.ComponentCallbackAttribute",
                IsValidGenerateCallbackTarget,
                GetGenerateCallbackClassTarget)
                .Where(g => g is not null)
                .Select((g, c) => g!.Value);

            context.RegisterSourceOutput(
                generateClassTargetProvider,
                GenerateCallbackHelpers);
        }

        private static bool IsValidGenerateCallbackTarget(SyntaxNode node, CancellationToken token)
        {
            if (node is not DelegateDeclarationSyntax delegateDeclarationSyntax)
                return false;

            // TODO: Add support for non-void return types?
            if (delegateDeclarationSyntax.ReturnType is not PredefinedTypeSyntax predefinedReturnType
                || !string.Equals(predefinedReturnType.Keyword.Text, "void", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private GenerateCallbackInfo? GetGenerateCallbackClassTarget(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
        {
            if (context.TargetNode is not DelegateDeclarationSyntax delegateDeclarationSyntax)
                return null;

            if (context.TargetSymbol is not INamedTypeSymbol namedTypeSymbol || namedTypeSymbol.ContainingNamespace.IsGlobalNamespace)
                return null;

            string? accessModifier = null;
            for (int i = 0; i < delegateDeclarationSyntax.Modifiers.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SyntaxToken syntaxToken = delegateDeclarationSyntax.Modifiers[i];
                if (string.Equals(syntaxToken.Text, "public", StringComparison.Ordinal))
                {
                    accessModifier = "public";
                    break;
                }
                else if (string.Equals(syntaxToken.Text, "internal", StringComparison.Ordinal))
                {
                    accessModifier = "internal";
                    break;
                }
            }

            if (accessModifier is null)
                return null;

            var parameterBuilder = ImmutableArray.CreateBuilder<CallbackParameterInfo>(delegateDeclarationSyntax.ParameterList.Parameters.Count);
            foreach (ParameterSyntax parameterSyntax in delegateDeclarationSyntax.ParameterList.Parameters)
            {
                cancellationToken.ThrowIfCancellationRequested();

                CallbackParameterModifierTypes? modifiers = null;

                // Parameter modifiers
                SyntaxTokenList modifiersList = parameterSyntax.Modifiers;
                if (modifiersList.Count == 0)
                {
                    modifiers = CallbackParameterModifierTypes.None;
                }
                else if (modifiersList.Count == 1)
                {
                    switch (modifiersList[0].Text)
                    {
                        case "ref":
                            modifiers = CallbackParameterModifierTypes.Ref;
                            break;
                        case "out":
                            modifiers = CallbackParameterModifierTypes.Out;
                            break;
                        case "in":
                            modifiers = CallbackParameterModifierTypes.In;
                            break;
                    }
                }
                else if (modifiersList.Count == 2)
                {
                    if (modifiersList[0].Text == "ref" || modifiersList[1].Text == "readonly")
                        modifiers = CallbackParameterModifierTypes.RefReadonly;
                }

                if (modifiers is null)
                    return null;

                // Parameter type
                if (parameterSyntax.Type is null)
                    return null;

                if (!TryGetTypeInfo(parameterSyntax.Type, out string? typeName, out bool isNullable) || typeName is null)
                    return null;

                parameterBuilder.Add(new CallbackParameterInfo(modifiers.Value, typeName, isNullable, parameterSyntax.Identifier.Text));
            }

            return new GenerateCallbackInfo(
                namedTypeSymbol.ContainingNamespace.ToDisplayString(),
                accessModifier,
                delegateDeclarationSyntax.Identifier.Text,
                "void",
                new EquatableImmutableArrayWrapper<CallbackParameterInfo>(parameterBuilder.ToImmutable()));

            // local function that helps get the type name
            bool TryGetTypeInfo(TypeSyntax typeSyntax, out string? typeName, out bool isNullable)
            {
                if (typeSyntax is PredefinedTypeSyntax predefinedTypeSyntax)
                {
                    typeName = predefinedTypeSyntax.Keyword.Text;
                    isNullable = false;
                    return true;
                }
                else if (typeSyntax is NameSyntax nameSyntax)
                {
                    SymbolInfo parameterSymbolInfo = context.SemanticModel.GetSymbolInfo(nameSyntax, cancellationToken);
                    ISymbol? symbol = parameterSymbolInfo.Symbol;
                    if (symbol is not null)
                    {
                        // We need the fully qualified name, otherwise we'll likely run into issues with missing using statements.
                        typeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        isNullable = false;
                        return true;
                    }
                }
                else if (typeSyntax is NullableTypeSyntax nullableTypeSyntax)
                {
                    if (TryGetTypeInfo(nullableTypeSyntax.ElementType, out typeName, out _))
                    {
                        isNullable = true;
                        return true;
                    }
                }

                typeName = null;
                isNullable = false;
                return false;
            }
        }

        private void GenerateCallbackHelpers(SourceProductionContext context, GenerateCallbackInfo generateCallbackInfo)
        {
            int parameterCount = generateCallbackInfo.Parameters.Array.Length;
            StringBuilder parametersBuilder = new();
            StringBuilder argumentsBuilder = new();

            for (int parameterIndex = 0; parameterIndex < generateCallbackInfo.Parameters.Array.Length; parameterIndex++)
            {
                var parameterInfo = generateCallbackInfo.Parameters.Array[parameterIndex];

                if (parameterIndex > 0)
                {
                    parametersBuilder.Append(", ");
                    argumentsBuilder.Append(", ");
                }

                switch (parameterInfo.Modifiers)
                {
                    case CallbackParameterModifierTypes.Ref:
                        parametersBuilder.Append("ref ");
                        argumentsBuilder.Append("ref ");
                        break;
                    case CallbackParameterModifierTypes.Out:
                        parametersBuilder.Append("out ");
                        argumentsBuilder.Append("out ");
                        break;
                    case CallbackParameterModifierTypes.In:
                        parametersBuilder.Append("in ");
                        argumentsBuilder.Append("in ");
                        break;
                    case CallbackParameterModifierTypes.RefReadonly:
                        parametersBuilder.Append("ref readonly ");
                        argumentsBuilder.Append("in ");
                        break;

                    case CallbackParameterModifierTypes.None:
                    default:
                        break;
                }

                parametersBuilder.Append($"{parameterInfo.TypeName}{(parameterInfo.IsNullable ? "?" : "")} {parameterInfo.Name}");
                argumentsBuilder.Append(parameterInfo.Name);
            }

            string parameters = parametersBuilder.ToString();
            string arguments = argumentsBuilder.ToString();

            StringBuilder sb = new();
            sb.AppendLine($$"""
                // <auto-generated>
                //     Generated by the {{nameof(ComponentCallbackGenerator)}}
                // </auto-generated>

                using SS.Core.ComponentInterfaces;
                using System;

                #nullable enable

                namespace {{generateCallbackInfo.Namespace}}
                {
                    {{generateCallbackInfo.AccessModifier}} static class {{generateCallbackInfo.CallbackName}}Extensions
                    {
                        extension({{generateCallbackInfo.CallbackName}})
                        {
                            /// <summary>
                            /// Registers a callback <paramref name="handler"/> on a <paramref name="broker"/>.
                            /// </summary>
                            /// <param name="broker">The broker to register on.</param>
                            /// <param name="handler">The handler to register.</param>
                            public static void Register(IComponentBroker broker, {{generateCallbackInfo.CallbackName}} handler)
                            {
                                broker?.RegisterCallback(handler);
                            }

                            /// <summary>
                            /// Unregisters a callback <paramref name="handler"/> from a <paramref name="handler"/>.
                            /// </summary>
                            /// <param name="broker">The broker to unregister from.</param>
                            /// <param name="handler">The handler to unregister.</param>
                            public static void Unregister(IComponentBroker broker, {{generateCallbackInfo.CallbackName}} handler)
                            {
                                broker?.UnregisterCallback(handler);
                            }

                            /// <summary>
                            /// Invokes a callback's registered handlers on a <paramref name="broker"/> and any parent broker(s).
                            /// </summary>
                            /// <param name="broker">The broker to invoke callback handlers on.</param>
                """);

            foreach (var parameterInfo in generateCallbackInfo.Parameters.Array)
            {
                sb.AppendLine($"""            /// <param name="{parameterInfo.Name}"><inheritdoc cref="{generateCallbackInfo.CallbackName}" path="/param[@name='{parameterInfo.Name}']"/></param>""");
            }

            sb.AppendLine($$"""
                            public static void Fire(IComponentBroker broker{{(parameterCount > 0 ? ", " : "")}}{{parameters}})
                            {
                                if (broker is null)
                                    return;

                                {{generateCallbackInfo.CallbackName}}? callbacks = broker.GetCallback<{{generateCallbackInfo.CallbackName}}>();
                                if (callbacks is not null)
                                    InvokeCallbacks(broker, callbacks{{(parameterCount > 0 ? ", " : "")}}{{arguments}});

                                if (broker.Parent is not null)
                                    Fire(broker.Parent{{(parameterCount > 0 ? ", " : "")}}{{arguments}});

                                // local helper (for recursion)
                                static void InvokeCallbacks(IComponentBroker broker, {{generateCallbackInfo.CallbackName}} callbacks{{(parameterCount > 0 ? ", " : "")}}{{parameters}})
                                {
                                    if (callbacks.HasSingleTarget)
                                    {
                                        try
                                        {
                                            callbacks.Invoke({{arguments}});
                                        }
                                        catch (Exception ex)
                                        {
                                            ILogManager? logManager = broker.GetInterface<ILogManager>();
                                            if (logManager is not null)
                                            {
                                                try
                                                {
                                                    logManager.Log(LogLevel.Error, $"Exception caught while processing callback {nameof({{generateCallbackInfo.CallbackName}})}. {ex}");
                                                }
                                                finally
                                                {
                                                    broker.ReleaseInterface(ref logManager);
                                                }
                                            }
                                            else
                                            {
                                                Console.Error.WriteLine($"Exception caught while processing callback {nameof({{generateCallbackInfo.CallbackName}})}. {ex}");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        foreach ({{generateCallbackInfo.CallbackName}} callback in Delegate.EnumerateInvocationList(callbacks))
                                        {
                                            InvokeCallbacks(broker, callback{{(parameterCount > 0 ? ", " : "")}}{{arguments}});
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                """);

            context.AddSource($"{generateCallbackInfo.Namespace}.{generateCallbackInfo.CallbackName}Extensions.g.cs", sb.ToString());
        }

        private readonly record struct GenerateCallbackInfo
        {
            public readonly string Namespace;
            public readonly string AccessModifier;
            public readonly string CallbackName;
            public readonly string ReturnType; // Maybe limit only to void? For now, don't care since all the callbacks are void.
            public readonly EquatableImmutableArrayWrapper<CallbackParameterInfo> Parameters;

            public GenerateCallbackInfo(
                string @namespace,
                string accessModifier,
                string callbackName,
                string returnType,
                EquatableImmutableArrayWrapper<CallbackParameterInfo> parameters)
            {
                Namespace = @namespace;
                AccessModifier = accessModifier;
                CallbackName = callbackName;
                ReturnType = returnType;
                Parameters = parameters;
            }
        }

        private readonly record struct CallbackParameterInfo
        {
            public readonly CallbackParameterModifierTypes Modifiers;
            public readonly string TypeName;
            public readonly bool IsNullable;
            public readonly string Name;

            public CallbackParameterInfo(CallbackParameterModifierTypes modifiers, string typeName, bool isNullable, string name)
            {
                Modifiers = modifiers;
                TypeName = typeName;
                IsNullable = isNullable;
                Name = name;
            }
        }

        private enum CallbackParameterModifierTypes
        {
            /// <summary>
            /// Pass by value
            /// </summary>
            None,
            Ref,
            Out,
            In,
            RefReadonly,

            // NOTE: params is also a modifier, but not one we care about
        }

        /// <summary>
        /// A wrapper around <see cref="ImmutableArray{T}"/> that supports equality checks.
        /// The <typeparamref name="T"/> type must also be equatable.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        private class EquatableImmutableArrayWrapper<T>(ImmutableArray<T> array) : IEquatable<EquatableImmutableArrayWrapper<T>> where T : IEquatable<T>
        {
            public readonly ImmutableArray<T> Array = array;

            public bool Equals(EquatableImmutableArrayWrapper<T> other)
            {
                return Array.SequenceEqual(other.Array);
            }
        }
    }
}
