namespace CommonCodeGenerator.SourceGenerator;

using System.Collections.Immutable;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

[Generator]
public sealed class ToStringGenerator : IIncrementalGenerator
{
    // ------------------------------------------------------------
    // Generator
    // ------------------------------------------------------------

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var compilationProvider = context.CompilationProvider;
        var classes = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => IsTargetSyntax(node),
                static (context, _) => GetTargetSyntax(context))
            .SelectMany(static (x, _) => x is not null ? ImmutableArray.Create(x) : [])
            .Collect();
        var optionProvider = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => GetOptions(provider));

        var providers = compilationProvider.Combine(classes).Combine(optionProvider);

        context.RegisterImplementationSourceOutput(
            providers,
            static (spc, source) => Execute(spc, source.Left.Left, source.Left.Right, source.Right));
    }

    private static bool IsTargetSyntax(SyntaxNode node) =>
        node is ClassDeclarationSyntax { AttributeLists.Count: > 0 };

    private static ClassDeclarationSyntax? GetTargetSyntax(GeneratorSyntaxContext context)
    {
        var classDeclarationSyntax = (ClassDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(classDeclarationSyntax) is not ITypeSymbol typeSymbol)
        {
            return null;
        }

        var hasAttribute = typeSymbol.GetAttributes()
            .Any(static x => x.AttributeClass!.ToDisplayString() == "CommonCodeGenerator.GenerateToStringAttribute" &&
                             x.ConstructorArguments.Length == 0);
        if (!hasAttribute)
        {
            return null;
        }

        return classDeclarationSyntax;
    }

    private static GeneratorOptions GetOptions(AnalyzerConfigOptionsProvider provider)
    {
        var options = new GeneratorOptions();

        // Mode
        var mode = OptionHelper.GetPropertyValue<string?>(provider.GlobalOptions, "ToStringMode");
        if (String.IsNullOrEmpty(mode) || String.Equals(mode, "Default", StringComparison.OrdinalIgnoreCase))
        {
            options.OutputClassName = true;
        }

        // OutputClassName
        var outputClassName = OptionHelper.GetPropertyValue<bool?>(provider.GlobalOptions, "ToStringOutputClassName");
        if (outputClassName.HasValue)
        {
            options.OutputClassName = outputClassName.Value;
        }

        // NullLiteral
        var nullLiteral = OptionHelper.GetPropertyValue<string?>(provider.GlobalOptions, "ToStringNullLiteral");
        if (!String.IsNullOrEmpty(nullLiteral))
        {
            options.NullLiteral = nullLiteral;
        }

        return options;
    }

    // ------------------------------------------------------------
    // Options
    // ------------------------------------------------------------

    private sealed class GeneratorOptions
    {
        public bool OutputClassName { get; set; }

        public string? NullLiteral { get; set; }
    }

    // ------------------------------------------------------------
    // Builder
    // ------------------------------------------------------------

    // ReSharper disable ConvertIfStatementToConditionalTernaryExpression
    private static void Execute(SourceProductionContext context, Compilation compilation, ImmutableArray<ClassDeclarationSyntax> classes, GeneratorOptions options)
    {
        var genericEnumerableSymbol = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");

        var filename = new StringBuilder();
        var source = new StringBuilder();

        foreach (var classDeclarationSyntax in classes)
        {
            // Check cancel
            context.CancellationToken.ThrowIfCancellationRequested();

            // Build metadata
            var classSemantic = compilation.GetSemanticModel(classDeclarationSyntax.SyntaxTree);
            var classSymbol = (ITypeSymbol)classSemantic.GetDeclaredSymbol(classDeclarationSyntax)!;

            var ns = classSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : classSymbol.ContainingNamespace.ToDisplayString();
            var className = classDeclarationSyntax.GetClassName();

            var properties = new List<IPropertySymbol>();
            var currentSymbol = classSymbol;
            while (currentSymbol != null)
            {
                properties.AddRange(
                    currentSymbol.GetMembers()
                        .OfType<IPropertySymbol>()
                        .Where(x => !x.GetAttributes()
                            .Any(attr => attr.AttributeClass?.ToDisplayString() == "CommonCodeGenerator.IgnoreToStringAttribute")));
                currentSymbol = currentSymbol.BaseType;
            }

            // Source
            source.AppendLine("// <auto-generated />");
            source.AppendLine("#nullable disable");

            // namespace
            if (!String.IsNullOrEmpty(ns))
            {
                source.Append("namespace ").Append(ns).AppendLine();
            }

            source.AppendLine("{");

            // class
            source.Append("    partial ").Append(classSymbol.IsValueType ? "struct " : "class ").Append(className).AppendLine();
            source.AppendLine("    {");

            // Method
            source.AppendLine("        public override string ToString()");
            source.AppendLine("        {");
            source.AppendLine("            var handler = new global::System.Runtime.CompilerServices.DefaultInterpolatedStringHandler(0, 0, default, stackalloc char[256]);");
            if (options.OutputClassName)
            {
                source.AppendLine($"            handler.AppendLiteral(\"{className} \");");
            }
            source.AppendLine("            handler.AppendLiteral(\"{ \");");

            var firstProperty = true;
            foreach (var property in properties)
            {
                if (firstProperty)
                {
                    firstProperty = false;
                }
                else
                {
                    source.AppendLine("            handler.AppendLiteral(\", \");");
                }

                var literal = property.Name + " = ";
                source.AppendLine($"            handler.AppendLiteral(\"{literal}\");");

                var (isEnumerable, isNullable) = GetPropertyType(property.Type, genericEnumerableSymbol);
                if (isEnumerable)
                {
                    source.AppendLine($"            if ({property.Name} is not null)");
                    source.AppendLine("            {");
                    source.AppendLine("                handler.AppendLiteral(\"[\");");
                    if (isNullable)
                    {
                        if (!String.IsNullOrEmpty(options.NullLiteral))
                        {
                            source.AppendLine($"                handler.AppendLiteral(String.Join(\", \", System.Linq.Enumerable.Select({property.Name}, static x => x?.ToString() ?? \"{options.NullLiteral}\")));");
                        }
                        else
                        {
                            source.AppendLine($"                handler.AppendLiteral(String.Join(\", \", System.Linq.Enumerable.Select({property.Name}, static x => x?.ToString())));");
                        }
                    }
                    else
                    {
                        source.AppendLine($"                handler.AppendLiteral(String.Join(\", \", System.Linq.Enumerable.Select({property.Name}, static x => x.ToString())));");
                    }
                    source.AppendLine("                handler.AppendLiteral(\"]\");");
                    source.AppendLine("            }");
                    if (!String.IsNullOrEmpty(options.NullLiteral))
                    {
                        source.AppendLine("            else");
                        source.AppendLine("            {");
                        source.AppendLine($"                handler.AppendLiteral(\"{options.NullLiteral}\");");
                        source.AppendLine("            }");
                    }
                }
                else
                {
                    if (isNullable)
                    {
                        if (!String.IsNullOrEmpty(options.NullLiteral))
                        {
                            source.AppendLine($"            if ({property.Name} is not null)");
                            source.AppendLine("            {");
                            source.AppendLine($"                handler.AppendFormatted({property.Name});");
                            source.AppendLine("            }");
                            source.AppendLine("            else");
                            source.AppendLine("            {");
                            source.AppendLine($"                handler.AppendLiteral(\"{options.NullLiteral}\");");
                            source.AppendLine("            }");
                        }
                        else
                        {
                            source.AppendLine($"            handler.AppendFormatted({property.Name});");
                        }
                    }
                    else
                    {
                        source.AppendLine($"            handler.AppendFormatted({property.Name});");
                    }
                }
            }

            source.AppendLine("            handler.AppendLiteral(\" }\");");
            source.AppendLine("            return handler.ToStringAndClear();");
            source.AppendLine("        }");

            source.AppendLine("    }");
            source.AppendLine("}");

            // Write
            context.AddSource(
                MakeRegistryFilename(filename, ns, className),
                SourceText.From(source.ToString(), Encoding.UTF8));

            source.Clear();
        }
    }
    // ReSharper restore ConvertIfStatementToConditionalTernaryExpression

    private static (bool IsEnumerable, bool IsNullable) GetPropertyType(ITypeSymbol typeSymbol, INamedTypeSymbol? genericEnumerableSymbol)
    {
        if (!typeSymbol.SpecialType.Equals(SpecialType.System_String))
        {
            if (typeSymbol is IArrayTypeSymbol arrayTypeSymbol)
            {
                var elementType = arrayTypeSymbol.ElementType;
                return (true, elementType.IsReferenceType || elementType.IsGenericType());
            }

            foreach (var @interface in typeSymbol.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition, genericEnumerableSymbol))
                {
                    var elementType = @interface.TypeArguments[0];
                    return (true, elementType.IsReferenceType || elementType.IsGenericType());
                }
            }
        }

        return (false, typeSymbol.IsReferenceType || typeSymbol.IsGenericType());
    }

    private static string MakeRegistryFilename(StringBuilder buffer, string ns, string className)
    {
        buffer.Clear();

        if (!String.IsNullOrEmpty(ns))
        {
            buffer.Append(ns.Replace('.', '_'));
            buffer.Append('_');
        }

        buffer.Append(className.Replace('<', '[').Replace('>', ']'));
        buffer.Append("_ToString.g.cs");

        return buffer.ToString();
    }
}
