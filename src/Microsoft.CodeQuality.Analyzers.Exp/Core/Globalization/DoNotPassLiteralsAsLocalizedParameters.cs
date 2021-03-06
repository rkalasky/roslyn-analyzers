﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Analyzer.Utilities;
using Analyzer.Utilities.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Operations.ControlFlow;
using Microsoft.CodeAnalysis.Operations.DataFlow;
using Microsoft.CodeAnalysis.Operations.DataFlow.CopyAnalysis;
using Microsoft.CodeAnalysis.Operations.DataFlow.PointsToAnalysis;
using Microsoft.CodeAnalysis.Operations.DataFlow.StringContentAnalysis;

namespace Microsoft.CodeQuality.Analyzers.Exp.Globalization
{
    /// <summary>
    /// CA1303: Do not pass literals as localized parameters
    /// A method passes a string literal as a parameter to a constructor or method in the .NET Framework class library and that string should be localizable.
    /// This warning is raised when a literal string is passed as a value to a parameter or property and one or more of the following cases is true:
    ///   1. The LocalizableAttribute attribute of the parameter or property is set to true.
    ///   2. The parameter or property name contains "Text", "Message", or "Caption".
    ///   3. The name of the string parameter that is passed to a Console.Write or Console.WriteLine method is either "value" or "format".
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public sealed class DoNotPassLiteralsAsLocalizedParameters : DiagnosticAnalyzer
    {
        internal const string RuleId = "CA1303";

        private static readonly LocalizableString s_localizableTitle = new LocalizableResourceString(nameof(MicrosoftGlobalizationAnalyzersResources.DoNotPassLiteralsAsLocalizedParametersTitle), MicrosoftGlobalizationAnalyzersResources.ResourceManager, typeof(MicrosoftGlobalizationAnalyzersResources));
        private static readonly LocalizableString s_localizableMessage = new LocalizableResourceString(nameof(MicrosoftGlobalizationAnalyzersResources.DoNotPassLiteralsAsLocalizedParametersMessage), MicrosoftGlobalizationAnalyzersResources.ResourceManager, typeof(MicrosoftGlobalizationAnalyzersResources));
        private static readonly LocalizableString s_localizableDescription = new LocalizableResourceString(nameof(MicrosoftGlobalizationAnalyzersResources.DoNotPassLiteralsAsLocalizedParametersDescription), MicrosoftGlobalizationAnalyzersResources.ResourceManager, typeof(MicrosoftGlobalizationAnalyzersResources));

        internal static DiagnosticDescriptor Rule = new DiagnosticDescriptor(RuleId,
                                                                             s_localizableTitle,
                                                                             s_localizableMessage,
                                                                             DiagnosticCategory.Globalization,
                                                                             DiagnosticHelpers.DefaultDiagnosticSeverity,
                                                                             isEnabledByDefault: DiagnosticHelpers.EnabledByDefaultIfNotBuildingVSIX,
                                                                             description: s_localizableDescription,
                                                                             helpLinkUri: "https://docs.microsoft.com/en-us/visualstudio/code-quality/ca1303-do-not-pass-literals-as-localized-parameters",
                                                                             customTags: WellKnownDiagnosticTags.Telemetry);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

            context.RegisterCompilationStartAction(compilationContext =>
            {
                INamedTypeSymbol localizableStateAttributeSymbol = WellKnownTypes.LocalizableAttribute(compilationContext.Compilation);
                INamedTypeSymbol conditionalAttributeSymbol = WellKnownTypes.ConditionalAttribute(compilationContext.Compilation);
                INamedTypeSymbol systemConsoleSymbol = WellKnownTypes.Console(compilationContext.Compilation);
                ImmutableHashSet<INamedTypeSymbol> typesToIgnore = GetTypesToIgnore(compilationContext.Compilation);

                compilationContext.RegisterOperationBlockStartAction(operationBlockStartContext =>
                {
                    if (!(operationBlockStartContext.OwningSymbol is IMethodSymbol containingMethod))
                    {
                        return;
                    }

                    var lazyStringContentResult = new Lazy<DataFlowAnalysisResult<StringContentBlockAnalysisResult, StringContentAbstractValue>>(
                        valueFactory: ComputeStringContentAnalysisResult, isThreadSafe: false);

                    operationBlockStartContext.RegisterOperationAction(operationContext =>
                    {
                        var argument = (IArgumentOperation)operationContext.Operation;
                        switch (argument.Parent?.Kind)
                        {
                            case OperationKind.Invocation:
                            case OperationKind.ObjectCreation:
                                AnalyzeArgument(argument.Parameter, containingPropertySymbolOpt: null, operation: argument, reportDiagnostic: operationContext.ReportDiagnostic);
                                return;
                        }
                    }, OperationKind.Argument);

                    operationBlockStartContext.RegisterOperationAction(operationContext =>
                    {
                        var propertyReference = (IPropertyReferenceOperation)operationContext.Operation;
                        if (propertyReference.Parent is IAssignmentOperation assignment &&
                            assignment.Target == propertyReference &&
                            !propertyReference.Property.IsIndexer &&
                            propertyReference.Property.SetMethod?.Parameters.Length == 1)
                        {
                            IParameterSymbol valueSetterParam = propertyReference.Property.SetMethod.Parameters[0];
                            AnalyzeArgument(valueSetterParam, propertyReference.Property, assignment, operationContext.ReportDiagnostic);
                        }
                    }, OperationKind.PropertyReference);

                    void AnalyzeArgument(IParameterSymbol parameter, IPropertySymbol containingPropertySymbolOpt, IOperation operation, Action<Diagnostic> reportDiagnostic)
                    {
                        if (ShouldBeLocalized(parameter, containingPropertySymbolOpt, localizableStateAttributeSymbol, conditionalAttributeSymbol, systemConsoleSymbol, typesToIgnore))
                        {
                            StringContentAbstractValue stringContentValue = lazyStringContentResult.Value[operation];
                            if (stringContentValue.IsLiteralState)
                            {
                                Debug.Assert(stringContentValue.LiteralValues.Count > 0);

                                // FxCop compat: Do not fire if the literal value came from a default parameter value
                                if (stringContentValue.LiteralValues.Count == 1 &&
                                    parameter.IsOptional &&
                                    parameter.ExplicitDefaultValue is string defaultValue &&
                                    defaultValue == stringContentValue.LiteralValues.Single())
                                {
                                    return;
                                }

                                // FxCop compat: Do not fire if none of the string literals have any non-control character.
                                if (!LiteralValuesHaveNonControlCharacters(stringContentValue.LiteralValues))
                                {
                                    return;
                                }

                                // FxCop compat: Filter out xml string literals.
                                var filteredStrings = stringContentValue.LiteralValues.Where(literal => !LooksLikeXmlTag(literal));
                                if (filteredStrings.Any())
                                {
                                    // Method '{0}' passes a literal string as parameter '{1}' of a call to '{2}'. Retrieve the following string(s) from a resource table instead: "{3}".
                                    var arg1 = containingMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                                    var arg2 = parameter.Name;
                                    var arg3 = parameter.ContainingSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                                    var arg4 = FormatLiteralValues(filteredStrings);
                                    var diagnostic = operation.CreateDiagnostic(Rule, arg1, arg2, arg3, arg4);
                                    reportDiagnostic(diagnostic);
                                }
                            }
                        }
                    }

                    DataFlowAnalysisResult<StringContentBlockAnalysisResult, StringContentAbstractValue> ComputeStringContentAnalysisResult()
                    {
                        foreach (var operationRoot in operationBlockStartContext.OperationBlocks)
                        {
                            IBlockOperation topmostBlock = operationRoot.GetTopmostParentBlock();
                            if (topmostBlock != null)
                            {
                                var cfg = ControlFlowGraph.Create(topmostBlock);
                                var wellKnownTypeProvider = WellKnownTypeProvider.GetOrCreate(operationBlockStartContext.Compilation);
                                var pointsToAnalysisResult = PointsToAnalysis.GetOrComputeResult(cfg, containingMethod, wellKnownTypeProvider);
                                var copyAnalysisResult = CopyAnalysis.GetOrComputeResult(cfg, containingMethod, wellKnownTypeProvider, pointsToAnalysisResultOpt: pointsToAnalysisResult);
                                // Do another analysis pass to improve the results from PointsTo and Copy analysis.
                                pointsToAnalysisResult = PointsToAnalysis.GetOrComputeResult(cfg, containingMethod, wellKnownTypeProvider, pointsToAnalysisResult, copyAnalysisResult);
                                return StringContentAnalysis.GetOrComputeResult(cfg, containingMethod, wellKnownTypeProvider, copyAnalysisResult, pointsToAnalysisResult);
                            }
                        }

                        return null;
                    }
                });
            });
        }

        private static ImmutableHashSet<INamedTypeSymbol> GetTypesToIgnore(Compilation compilation)
        {
            var builder = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>();

            var xmlWriter = WellKnownTypes.XmlWriter(compilation);
            if (xmlWriter != null)
            {
                builder.Add(xmlWriter);
            }

            var webUILiteralControl = WellKnownTypes.WebUILiteralControl(compilation);
            if (webUILiteralControl != null)
            {
                builder.Add(webUILiteralControl);
            }

            var unitTestingAssert = WellKnownTypes.UnitTestingAssert(compilation);
            if (unitTestingAssert != null)
            {
                builder.Add(unitTestingAssert);
            }

            var unitTestingCollectionAssert = WellKnownTypes.UnitTestingCollectionAssert(compilation);
            if (unitTestingCollectionAssert != null)
            {
                builder.Add(unitTestingCollectionAssert);
            }

            var unitTestingCollectionStringAssert = WellKnownTypes.UnitTestingCollectionStringAssert(compilation);
            if (unitTestingCollectionStringAssert != null)
            {
                builder.Add(unitTestingCollectionStringAssert);
            }

            return builder.ToImmutable();
        }

        private static bool ShouldBeLocalized(
            IParameterSymbol parameterSymbol,
            IPropertySymbol containingPropertySymbolOpt,
            INamedTypeSymbol localizableStateAttributeSymbol,
            INamedTypeSymbol conditionalAttributeSymbol,
            INamedTypeSymbol systemConsoleSymbol,
            ImmutableHashSet<INamedTypeSymbol> typesToIgnore)
        {
            Debug.Assert(parameterSymbol.ContainingSymbol.Kind == SymbolKind.Method);

            if (parameterSymbol.Type.SpecialType != SpecialType.System_String)
            {
                return false;
            }

            // Verify LocalizableAttributeState
            if (localizableStateAttributeSymbol != null)
            {
                LocalizableAttributeState localizableAttributeState = GetLocalizableAttributeState(parameterSymbol, localizableStateAttributeSymbol);
                switch (localizableAttributeState)
                {
                    case LocalizableAttributeState.False:
                        return false;

                    case LocalizableAttributeState.True:
                        return true;

                    default:
                        break;
                }
            }

            // FxCop compat checks.
            if (typesToIgnore.Contains(parameterSymbol.ContainingType) ||
                parameterSymbol.ContainingSymbol.HasAttribute(conditionalAttributeSymbol))
            {
                return false;
            }

            // FxCop compat: For overrides, check for localizability of the corresponding parameter in the overridden method.
            var method = (IMethodSymbol)parameterSymbol.ContainingSymbol;
            if (method.IsOverride &&
                method.OverriddenMethod.Parameters.Length == method.Parameters.Length)
            {
                int parameterIndex = method.GetParameterIndex(parameterSymbol);
                IParameterSymbol overridenParameter = method.OverriddenMethod.Parameters[parameterIndex];
                if (overridenParameter.Type == parameterSymbol.Type)
                {
                    return ShouldBeLocalized(overridenParameter, containingPropertySymbolOpt, localizableStateAttributeSymbol, conditionalAttributeSymbol, systemConsoleSymbol, typesToIgnore);
                }
            }

            // FxCop compat: If a localizable attribute isn't defined then fall back to name heuristics.
            bool IsLocalizableByNameHeuristic(ISymbol symbol) =>
                symbol.Name.Equals("message", StringComparison.OrdinalIgnoreCase) ||
                symbol.Name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                symbol.Name.Equals("caption", StringComparison.OrdinalIgnoreCase);

            if (IsLocalizableByNameHeuristic(parameterSymbol) ||
                containingPropertySymbolOpt != null && IsLocalizableByNameHeuristic(containingPropertySymbolOpt))
            {
                return true;
            }

            if (method.ContainingType.Equals(systemConsoleSymbol) &&
                (method.Name.Equals("Write", StringComparison.Ordinal) ||
                 method.Name.Equals("WriteLine", StringComparison.Ordinal)) &&
                (parameterSymbol.Name.Equals("format", StringComparison.OrdinalIgnoreCase) ||
                 parameterSymbol.Name.Equals("value", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        private static LocalizableAttributeState GetLocalizableAttributeState(ISymbol symbol, INamedTypeSymbol localizableAttributeTypeSymbol)
        {
            Debug.Assert(localizableAttributeTypeSymbol != null);
            if (symbol == null)
            {
                return LocalizableAttributeState.Undefined;
            }

            LocalizableAttributeState localizedState = GetLocalizableAttributeStateCore(symbol.GetAttributes(), localizableAttributeTypeSymbol);
            if (localizedState != LocalizableAttributeState.Undefined)
            {
                return localizedState;
            }

            ISymbol containingSymbol = (symbol as IMethodSymbol)?.AssociatedSymbol is IPropertySymbol propertySymbol ? propertySymbol : symbol.ContainingSymbol;
            return GetLocalizableAttributeState(containingSymbol, localizableAttributeTypeSymbol);
        }

        private static LocalizableAttributeState GetLocalizableAttributeStateCore(ImmutableArray<AttributeData> attributeList, INamedTypeSymbol localizableAttributeTypeSymbol)
        {
            Debug.Assert(localizableAttributeTypeSymbol != null);

            var localizableAttribute = attributeList.FirstOrDefault(attr => localizableAttributeTypeSymbol.Equals(attr.AttributeClass));
            if (localizableAttribute != null &&
                localizableAttribute.AttributeConstructor.Parameters.Length == 1 &&
                localizableAttribute.AttributeConstructor.Parameters[0].Type.SpecialType == SpecialType.System_Boolean &&
                localizableAttribute.ConstructorArguments.Length == 1 &&
                localizableAttribute.ConstructorArguments[0].Kind == TypedConstantKind.Primitive &&
                localizableAttribute.ConstructorArguments[0].Value is bool isLocalizable)
            {
                return isLocalizable ? LocalizableAttributeState.True : LocalizableAttributeState.False;
            }

            return LocalizableAttributeState.Undefined;
        }

        private static string FormatLiteralValues(IEnumerable<string> literalValues)
        {
            var literals = new StringBuilder();
            foreach (string literal in literalValues.Order())
            {
                if (literals.Length > 0)
                {
                    literals.Append(", ");
                }

                literals.Append(literal);
            }

            return literals.ToString();
        }

        /// <summary>
        /// Returns true if the given string looks like an XML/HTML tag
        /// </summary>
        private static bool LooksLikeXmlTag(string literal)
        {
            // Call the trim function to remove any spaces around the beginning and end of the string so we can more accurately detect
            // XML strings
            string trimmedLiteral = literal.Trim();
            return trimmedLiteral.Length > 2 && trimmedLiteral[0] == '<' && trimmedLiteral[trimmedLiteral.Length - 1] == '>';
        }

        /// <summary>
        /// Returns true if any character in literalValues is not a control character
        /// </summary>
        private static bool LiteralValuesHaveNonControlCharacters(IEnumerable<string> literalValues)
        {
            foreach (string literal in literalValues)
            {
                foreach (char ch in literal)
                {
                    if (!char.IsControl(ch))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
