﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SS.Core;

namespace SS.SourceGeneration.Tests
{
    public static class TestHelper
    {
        public static Task VerifyConfigHelpValuesGenerator(string source)
        {
            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);

            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
                .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
                .Concat([MetadataReference.CreateFromFile(typeof(GenerateConfigHelpConstantsAttribute).Assembly.Location)]);

            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyName: "Tests",
                syntaxTrees: [syntaxTree],
                references: references);

            ConfigHelpConstantsGenerator generator = new();

            GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

            driver = driver.RunGenerators(compilation);

            return Verifier
                .Verify(driver)
                .UseDirectory("Snapshots");
        }
    }
}
