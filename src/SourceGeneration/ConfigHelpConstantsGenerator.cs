using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

namespace SS.SourceGeneration;

/// <summary>
/// Generates classes containing constants from [ConfigHelp] and [ConfigHelpAttribute&lt;T&gt;] attributes.
/// </summary>
/// <remarks>
/// The SS.Core.GenerateConfigHelpConstantsAttribute is used as marker to designate which partial class to generate with nested classes containing the constants.
/// It must be a static partial class, and it can have either the public or internal access modifier.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public class ConfigHelpConstantsGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var generateClassTargetProvider = context.SyntaxProvider.ForAttributeWithMetadataName(
            "SS.Core.GenerateConfigHelpConstantsAttribute",
            predicate: IsValidGenerateClassTarget,
            transform: GetGenerateClassTarget
        ).Where(static (target) => target is not null).Select((nullableTarget, _) => nullableTarget!.Value);

        var combinedClassTargetsProvider = generateClassTargetProvider.Collect();

        var genericAttributesValuesProvider = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: IsValidConfigHelpGenericAttribute,
            transform: GetConfigHelpAttributeInfo
        ).Where(static (info) => info is not null).Select((nullableTarget, _) => nullableTarget!.Value);

        var genericAttributesValueProvider = genericAttributesValuesProvider.Collect();

        var nonGenericAttributesValuesProvider = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: IsValidConfigHelpAttribute,
            transform: GetConfigHelpAttributeInfo
        ).Where(static (info) => info is not null).Select((nullableTarget, _) => nullableTarget!.Value);

        var nonGenericAttributesValueProvider = nonGenericAttributesValuesProvider.Collect();

        var combinedAllAttributesValueProvider = nonGenericAttributesValueProvider.Combine(genericAttributesValueProvider);
        var allAttributesValuesProvider = combinedAllAttributesValueProvider.SelectMany(UnionAttributes);
        var allAttributesValueProvider = allAttributesValuesProvider.Collect();

        // TODO: maybe a more effcient way, but need IEquatable collection
        //var configHelpAttributeInfoCollectionProvider = context.SyntaxProvider.ForAttributeWithMetadataName(
        //    "SS.Core.ConfigHelpAttribute`1", // ConfigHelpAttribute<T>
        //    predicate: static (_, _) => true, // Allowed on all types of targets
        //    transform: static (context, _) => // Transform into a collection of ConfigHelpAttributeInfo, the collection would need to be IEquatable though
        //);

        var constantsProvider = combinedClassTargetsProvider.Combine(allAttributesValueProvider);

        context.RegisterSourceOutput(
            constantsProvider,
            GenerateConfigHelpConstants);
    }

    private ImmutableArray<ConfigHelpAttributeInfo> UnionAttributes((ImmutableArray<ConfigHelpAttributeInfo> Left, ImmutableArray<ConfigHelpAttributeInfo> Right) tuple, CancellationToken token)
    {
        return [.. tuple.Left, .. tuple.Right];
    }

    private static bool IsValidGenerateClassTarget(SyntaxNode syntaxNode, CancellationToken cancellationToken)
    {
        // only allow: static partial class
        if (syntaxNode is not ClassDeclarationSyntax classDeclarationSyntax)
            return false;

        bool isStatic = false;
        bool isPartial = false;

        for (int i = 0; i < classDeclarationSyntax.Modifiers.Count; i++)
        {
            SyntaxToken syntaxToken = classDeclarationSyntax.Modifiers[i];
            if (string.Equals(syntaxToken.Text, "static", StringComparison.Ordinal))
                isStatic = true;
            else if (string.Equals(syntaxToken.Text, "partial", StringComparison.Ordinal))
                isPartial = true;
        }
        

        return isStatic && isPartial;
    }

    private static GenerateClassTarget? GetGenerateClassTarget(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.TargetNode is not ClassDeclarationSyntax classDeclarationSyntax)
            return null;

        string? accessModifier = null;
        for (int i = 0; i < classDeclarationSyntax.Modifiers.Count; i++)
        {
            SyntaxToken syntaxToken = classDeclarationSyntax.Modifiers[i];
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

        AttributeData attributeData = context.Attributes[0];

        // Scope
        TypedConstant scopeTypedConstant = attributeData.ConstructorArguments[0];
        if (scopeTypedConstant.Value is not int scopeInt)
            return null;

        string scope;
        if (scopeInt == 0)
            scope = "Global";
        else if (scopeInt == 1)
            scope = "Arena";
        else
            return null;

        // FileName
        TypedConstant fileNameTypedConstant = attributeData.ConstructorArguments[1];
        string? fileName = fileNameTypedConstant.Value as string;

        if (context.TargetSymbol is not INamedTypeSymbol namedTypeSymbol)
            return null;

        return new GenerateClassTarget(
            namedTypeSymbol.ContainingNamespace.ToDisplayString(),
            namedTypeSymbol.Name,
            accessModifier,
            scope,
            fileName);
    }

    private static bool IsValidConfigHelpAttribute(SyntaxNode syntaxNode, CancellationToken cancellationToken)
    {
        // ConfigHelp
        if (syntaxNode is not AttributeSyntax attributeSyntax)
            return false;

        if (attributeSyntax.Name is not IdentifierNameSyntax identifierNameSyntax)
            return false;

        if (identifierNameSyntax.Identifier.Value is not string attributeName)
            return false;

        if (!string.Equals(attributeName, "ConfigHelp", StringComparison.Ordinal))
            return false;

        return true;
    }

    private static bool IsValidConfigHelpGenericAttribute(SyntaxNode syntaxNode, CancellationToken cancellationToken)
    {
        // ConfigHelp<T>
        if (syntaxNode is not AttributeSyntax attributeSyntax)
            return false;

        if (attributeSyntax.Name is not GenericNameSyntax genericNameSyntax)
            return false;

        if (genericNameSyntax.Identifier.Value is not string attributeName)
            return false;

        if (!string.Equals(attributeName, "ConfigHelp", StringComparison.Ordinal))
            return false;

        if (genericNameSyntax.TypeArgumentList.Arguments.Count != 1)
            return false;

        return true;
    }

    private static ConfigHelpAttributeInfo? GetConfigHelpAttributeInfo(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        AttributeSyntax attributeSyntax = (AttributeSyntax)context.Node;

        string type;
        if (attributeSyntax.Name is GenericNameSyntax genericNameSyntax)
        {
            if (genericNameSyntax.TypeArgumentList.Arguments[0] is not PredefinedTypeSyntax predefinedTypeSyntax)
                return null;

            if (predefinedTypeSyntax.Keyword.Value is not string typeArg)
                return null;

            type = typeArg;
        }
        else
        {
            type = "string";
        }

        SymbolInfo attributeSymbolInfo = context.SemanticModel.GetSymbolInfo(attributeSyntax, cancellationToken);
        ISymbol? attributeSymbol = attributeSymbolInfo.Symbol;
        if (attributeSymbol is null)
            return null;

        if (attributeSymbol is not IMethodSymbol attributeContructorSymbol)
            return null;

        var attributeArgumentListSyntax = attributeSyntax.ArgumentList;
        if (attributeArgumentListSyntax is null || attributeArgumentListSyntax.Arguments.Count < 3)
            return null;
        
        string? section = null;
        string? key = null;
        string? scope = null;
        string? fileName = null;
        string? defaultValue = null;
        string? min = null;
        string? max = null;

        for (int i = 0; i < attributeArgumentListSyntax.Arguments.Count; i++)
        {
            AttributeArgumentSyntax attributeArgumentSyntax = attributeArgumentListSyntax.Arguments[i];
            string name;

            if (i < attributeContructorSymbol.Parameters.Length)
            {
                name = attributeContructorSymbol.Parameters[i].Name;
            }
            else
            {
                if (attributeArgumentSyntax.NameEquals is not null && attributeArgumentSyntax.NameEquals?.Name.Identifier.Value is string nameEqualsStr)
                {
                    name = nameEqualsStr;
                }
                else
                {
                    continue;
                }
            }

            string? value = null;

            if (attributeArgumentSyntax.Expression is LiteralExpressionSyntax literalExpressionSyntax
                && literalExpressionSyntax.Token.Value is string literalExpressionStr)
            {
                value = literalExpressionStr;
            }
            else if (attributeArgumentSyntax.Expression is MemberAccessExpressionSyntax memberAccessExpressionSyntax)
            {
                SymbolInfo symbolInfo = context.SemanticModel.GetSymbolInfo(memberAccessExpressionSyntax);
                ISymbol? symbol = symbolInfo.Symbol;
                if (symbol is IFieldSymbol fieldSymbol && fieldSymbol.HasConstantValue)
                {
                    value = fieldSymbol.ConstantValue.ToString();
                }
                else if (memberAccessExpressionSyntax.Name.Identifier.Value is string memberAccessExpressionStr)
                {
                    value = memberAccessExpressionStr;
                }
                else
                {
                    continue;
                }
            }
            else if (attributeArgumentSyntax.Expression is IdentifierNameSyntax identifierNameSyntax)
            {
                SymbolInfo symbolInfo = context.SemanticModel.GetSymbolInfo(identifierNameSyntax, cancellationToken);
                ISymbol? symbol = symbolInfo.Symbol;
                if (symbol is IFieldSymbol fieldSymbol)
                {
                    if (fieldSymbol.HasConstantValue && fieldSymbol.ConstantValue is string fieldSymbolStr)
                    {
                        value = fieldSymbolStr;
                    }
                }
            }
            else
            {
                value = attributeArgumentSyntax.Expression.ToFullString();
            }

            switch (name)
            {
                case "Section":
                case "section":
                    section = value;
                    break;

                case "Key":
                case "key":
                    key = value;
                    break;

                case "Scope":
                case "scope":
                    if (value == "0")
                        scope = "Global";
                    else if (value == "1")
                        scope = "Arena";
                    else
                        scope = value;
                    break;

                case "FileName":
                case "fileName":
                    fileName = value;
                    break;

                case "Default":
                case "DefaultValue":
                    defaultValue = value;
                    break;

                case "Min":
                    min = value;
                    break;

                case "Max":
                    max = value;
                    break;

                default:
                    break;
            }
        }

        if (scope is null || section is null || key is null)
            return null;

        if (section.Contains("-"))
            section = section.Replace('-', '_');

        if (key.Contains("-"))
            key = key.Replace('-', '_');

        if (!section.Any(c => c < 'a' || c > 'z'))
            section = "@" + section;

        if (!key.Any(c => c < 'a' || c > 'z'))
            key = "@" + key;

        return new ConfigHelpAttributeInfo(scope, fileName, section, key, type, defaultValue, min, max);
    }

    private static void GenerateConfigHelpConstants(SourceProductionContext context, (ImmutableArray<GenerateClassTarget> Left, ImmutableArray<ConfigHelpAttributeInfo> Right) data)
    {
        ImmutableArray<GenerateClassTarget> generateClassTargets = data.Left.Sort(
            (left, right) =>
            {
                // Namespace asc
                int ret = left.Namespace.CompareTo(right.Namespace);
                if (ret != 0)
                    return ret;

                // ClassName asc
                return left.ClassName.CompareTo(right.ClassName);
            }
        );

        ImmutableArray<ConfigHelpAttributeInfo> configHelpAttributes = data.Right.Sort(
            (left, right) =>
            {
                // Section asc
                int ret = left.Section.CompareTo(right.Section);
                if (ret != 0)
                    return ret;

                // Key asc
                return left.Key.CompareTo(right.Key);
            }
        );

        StringBuilder sb = new();
        sb.AppendLine($"""
            // <auto-generated>
            //     Generated by the {nameof(ConfigHelpConstantsGenerator)}
            // </auto-generated>
            """);

        foreach (var target in generateClassTargets)
        {
            sb.AppendFormat($"namespace {target.Namespace}");
            sb.AppendLine();

            sb.Append('{');
            sb.AppendLine();

            sb.AppendFormat($"    {target.AccessModifier} static partial class {target.ClassName}");
            sb.AppendLine();

            sb.Append("    {");
            sb.AppendLine();

            string targetFileName = target.FileName ?? (target.Scope == "Global" ? "global.conf" : "arena.conf");

            string? currentSection = null;

            foreach (var attribute in configHelpAttributes)
            {
                if (!string.Equals(attribute.Scope, target.Scope, StringComparison.Ordinal))
                    continue;

                string attributeFileName = attribute.FileName ?? (attribute.Scope == "Global" ? "global.conf" : "arena.conf");

                if (!string.Equals(targetFileName, attributeFileName, StringComparison.Ordinal))
                    continue;

                if (!string.Equals(currentSection, attribute.Section, StringComparison.Ordinal))
                {
                    if (currentSection is not null)
                    {
                        sb.Append("        }");
                        sb.AppendLine();
                    }

                    currentSection = attribute.Section;

                    sb.AppendFormat($"        public static partial class {attribute.Section}");
                    sb.AppendLine();

                    sb.Append("        {");
                    sb.AppendLine();
                }

                sb.AppendFormat($"            public static partial class {attribute.Key}");
                sb.AppendLine();

                sb.Append("            {");
                sb.AppendLine();

                if (attribute.DefaultValue is not null)
                {
                    if (string.Equals(attribute.Type, "string", StringComparison.Ordinal))
                    {
                        sb.AppendFormat($"                public const {attribute.Type} Default = \"{attribute.DefaultValue}\";");
                    }
                    else
                    {
                        sb.AppendFormat($"                public const {attribute.Type} Default = {attribute.DefaultValue};");
                    }

                    sb.AppendLine();
                }

                if (attribute.Min is not null)
                {
                    sb.AppendFormat($"                public const {attribute.Type} Min = {attribute.Min};");
                    sb.AppendLine();
                }

                if (attribute.Max is not null)
                {
                    sb.AppendFormat($"                public const {attribute.Type} Max = {attribute.Max};");
                    sb.AppendLine();
                }

                sb.Append("            }");
                sb.AppendLine();
            }

            if (currentSection is not null)
            {
                sb.Append("        }");
                sb.AppendLine();
            }

            sb.Append("    }");
            sb.AppendLine();

            sb.Append('}');
            sb.AppendLine();
        }

        context.AddSource("ConfigHelpConstants.g.cs", sb.ToString());
    }

    private readonly record struct GenerateClassTarget
    {
        public readonly string Namespace;
        public readonly string ClassName;
        public readonly string AccessModifier;

        public readonly string Scope;
        public readonly string? FileName;

        public GenerateClassTarget(string @namespace, string className, string accessModifier, string scope, string? fileName)
        {
            Namespace = @namespace;
            ClassName = className;
            AccessModifier = accessModifier;
            Scope = scope;
            FileName = fileName;
        }
    }

    public readonly record struct ConfigHelpAttributeInfo
    {
        public readonly string Scope; // Global or Arena
        public readonly string? FileName; // The file of the setting. null for global.conf or arena.conf

        public readonly string Section;
        public readonly string Key;

        public readonly string Type;

        public readonly string? DefaultValue;
        public readonly string? Min;
        public readonly string? Max;

        public ConfigHelpAttributeInfo(string scope, string? fileName, string section, string key, string type, string? defaultValue, string? min, string? max)
        {
            Scope = scope;
            FileName = fileName;
            Section = section;
            Key = key;
            Type = type;
            DefaultValue = defaultValue;
            Min = min;
            Max = max;
        }
    }
}
