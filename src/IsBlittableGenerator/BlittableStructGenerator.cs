using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace IsBlittableGenerator
{
    /// <summary>
    /// A source generator that generates a static property IsBlittable for each struct with the StructLayout attribute.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public class BlittableStructGenerator : IIncrementalGenerator
    {
        /// <summary>
        /// Called to initialize the generator and register generation steps via callbacks on the context.
        /// </summary>
        /// <param name="context"></param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Register a syntax provider to identify structs with StructLayout attribute
            var candidateStructs = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (node, cancellationToken) =>
                        node is StructDeclarationSyntax structDecl &&
                        structDecl.Modifiers.Any(SyntaxKind.PartialKeyword) &&
                        structDecl.AttributeLists.Any(HasSequentialLayout),
                    transform: (syntaxContext, cancellationToken) => (StructDeclarationSyntax)syntaxContext.Node)
                .Where(syntax => syntax != null);

            // Combine the syntax provider with the CompilationProvider to access semantic model
            var compilationAndStructs = context.CompilationProvider.Combine(candidateStructs.Collect());

            // Apply a transformation to generate IsBlittable method for each candidate struct
            var generated = compilationAndStructs.Select((pair, cancellationToken) =>
            {
                var (compilation, structs) = pair;

                Dictionary<INamedTypeSymbol, bool> isBlittables = new Dictionary<INamedTypeSymbol, bool>(SymbolEqualityComparer.Default);
                KeyValuePair<string, string>[] results = ArrayPool<KeyValuePair<string, string>>.Shared.Rent(structs.Length + 1);
                int lastIndex = 0;
                foreach (StructDeclarationSyntax structSyntax in structs)
                {
                    SemanticModel model = compilation.GetSemanticModel(structSyntax.SyntaxTree);
                    INamedTypeSymbol symbol = model.GetDeclaredSymbol(structSyntax);

                    if (symbol == null)
                        continue;

                    bool isBlittable = IsBlittable(symbol, isBlittables);
                    isBlittables[symbol] = isBlittable;

                    results[lastIndex] = GenerateIsBlittableProperty(symbol, isBlittable);
                    lastIndex++;
                }

                // Generate the BlittableTypes class
                results[lastIndex] = GenerateExtensionClass(isBlittables);
                lastIndex++;

                return new ArraySegment<KeyValuePair<string, string>>(results, 0, lastIndex);
            });

            // Output the generated source code
            context.RegisterSourceOutput(generated, (productionContext, sources) =>
            {
                foreach (KeyValuePair<string, string> source in sources)
                {
                    productionContext.AddSource($"{source.Key}.g.cs", SourceText.From(source.Value, Encoding.UTF8));
                }

                ArrayPool<KeyValuePair<string, string>>.Shared.Return(sources.Array, true);
            });
        }

        private static bool IsBlittable(ITypeSymbol type, Dictionary<INamedTypeSymbol, bool> visited)
        {
            if (!(type is INamedTypeSymbol namedType) || namedType.TypeKind != TypeKind.Struct || !HasSequentialLayout(namedType))
            {
                return false;
            }

            if (visited.TryGetValue(namedType, out bool existing))
            {
                return existing;
            }

            // Prevent recursive loop
            visited[namedType] = false;

            foreach (IFieldSymbol field in namedType.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic)
                {
                    continue;
                }

                ITypeSymbol fieldType = field.Type;
                if (IsBuiltInBlittable(fieldType))
                {
                    continue;
                }

                if (fieldType is INamedTypeSymbol nestedNamed)
                {
                    switch (nestedNamed.TypeKind)
                    {
                        case TypeKind.Struct when IsBlittable(nestedNamed, visited):
                        case TypeKind.Enum when IsBuiltInBlittable(nestedNamed.EnumUnderlyingType):
                            continue;
                    }
                }

                return false;
            }

            visited[namedType] = true;
            return true;
        }

        private static bool IsBuiltInBlittable(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                //case SpecialType.System_Boolean:
                //case SpecialType.System_Char:
                //case SpecialType.System_Enum: // The field for the enum type is None, not System_Enum.
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_IntPtr:
                case SpecialType.System_UIntPtr:
                    return true;
                default:
                    return false;
            }
        }

        private static bool HasSequentialLayout(AttributeListSyntax attributeListSyntax)
        {
            foreach (AttributeSyntax attribute in attributeListSyntax.Attributes)
            {
                if (attribute.Name.ToString() == "StructLayout")
                {
                    AttributeArgumentSyntax argument = attribute.ArgumentList?.Arguments.FirstOrDefault();
                    if (argument != null && argument.GetText().ToString() == "LayoutKind.Sequential")
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasSequentialLayout(INamedTypeSymbol symbol)
        {
            foreach (AttributeData attribute in symbol.GetAttributes())
            {
                if (attribute.AttributeClass?.Name == "StructLayoutAttribute"
                    && attribute.ConstructorArguments[0].Value is int value
                    && value == (int)System.Runtime.InteropServices.LayoutKind.Sequential)
                {
                    return true;
                }
            }

            return false;
        }

        private static KeyValuePair<string, string> GenerateIsBlittableProperty(INamedTypeSymbol typeSymbol, bool isBlittable)
        {
            string newLine = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "\r\n" : "\n";
            string ns = typeSymbol.ContainingNamespace.IsGlobalNamespace ? "" : $"namespace {typeSymbol.ContainingNamespace} {newLine}{{";
            string nsEnd = typeSymbol.ContainingNamespace.IsGlobalNamespace ? "" : "}";

            string extensionClass =
$@"// <auto-generated>
//     This code was generated by a Source Generator.
//     Changes to this file may cause incorrect behavior and will be lost if the code is regenerated.
// </auto-generated>

{ns}
    partial struct {typeSymbol.Name}
    {{
        /// <summary>
        /// Returns true if the struct is blittable, false otherwise. <br />
        /// It will aways return {isBlittable.ToString().ToLowerInvariant()}.
        /// </summary>
        public static bool IsBlittable
        {{
            get
            {{
                return {isBlittable.ToString().ToLowerInvariant()};
            }}
        }}
    }}
{nsEnd}";

            return new KeyValuePair<string, string>(typeSymbol.Name + "_IsBlittable", extensionClass);
        }

        private static KeyValuePair<string, string> GenerateExtensionClass(Dictionary<INamedTypeSymbol, bool> isBlittables)
        {
            StringBuilder sb = new StringBuilder();

            int index = 0;
            foreach (KeyValuePair<INamedTypeSymbol, bool> kvp in isBlittables)
            {
                string fullTypeName = kvp.Key.ContainingNamespace.IsGlobalNamespace ? kvp.Key.Name : $"{kvp.Key.ContainingNamespace}.{kvp.Key.Name}";
                sb.Append($"            {{ typeof({fullTypeName}), {kvp.Value.ToString().ToLowerInvariant()} }}");

                if (index < isBlittables.Count - 1)
                    sb.AppendLine(",");

                index++;
            }

            // Generate static readonly dictionary in the BlittableTypes class
            string extensionClass = $@"
// <auto-generated>
//     This code was generated by a Source Generator.
//     Changes to this file may cause incorrect behavior and will be lost if the code is regenerated.
// </auto-generated>

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace IsBlittableGenerator
{{
    internal static class BlittableTypes
    {{
        private static readonly Dictionary<Type, bool> IsBlittableMap = new Dictionary<Type, bool>()
        {{
{sb}
        }};

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsBlittable<T>() where T : struct
        {{
            return IsBlittableMap.TryGetValue(typeof(T), out bool isBlittable) ? isBlittable : false;
        }}
    }}
}}
";
            return new KeyValuePair<string, string>("IsBlittableGenerator.BlittableTypes", extensionClass);
        }
    }
}
