using System.Collections.Immutable;
using System.Diagnostics;
using InteropGenerator.Helpers;
using InteropGenerator.Models;

namespace InteropGenerator.Generator;

public sealed partial class InteropGenerator {
    private static string RenderStructInfo(StructInfo structInfo, CancellationToken token) {
        using IndentedTextWriter writer = new();
        // write file header
        writer.WriteLine("// <auto-generated/>");

        // write namespace 
        if (structInfo.Namespace.Length > 0) {
            writer.WriteLine($"namespace {structInfo.Namespace};");
            writer.WriteLine();
        }

        // write opening struct hierarchy in reverse order
        // note we do not need to specify the accessibility here since a partial declared with no accessibility uses the other partial
        for (int i = structInfo.Hierarchy.Length - 1; i >= 0; i--) {
            writer.WriteLine($"unsafe partial struct {structInfo.Hierarchy[i]}");
            writer.WriteLine("{");
            writer.IncreaseIndent();
        }

        // write addresses for resolver
        if (structInfo.HasSignatures()) {
            RenderAddresses(structInfo, writer);
            token.ThrowIfCancellationRequested();
        }

        if (structInfo.HasVirtualTable()) {
            RenderVirtualTable(structInfo, writer);
            token.ThrowIfCancellationRequested();
        }

        // write delegate types
        if (!structInfo.MemberFunctions.IsEmpty || !structInfo.VirtualFunctions.IsEmpty) {
            RenderDelegateTypes(structInfo, writer);
            token.ThrowIfCancellationRequested();
        }

        // write member function pointers & method bodies
        if (!structInfo.MemberFunctions.IsEmpty) {
            RenderMemberFunctions(structInfo, writer);
            token.ThrowIfCancellationRequested();
        }

        // write virtual function method bodies
        if (!structInfo.VirtualFunctions.IsEmpty) {
            RenderVirtualFunctions(structInfo, writer);
            token.ThrowIfCancellationRequested();
        }

        // write static address method bodies
        if (!structInfo.StaticAddresses.IsEmpty) {
            RenderStaticAddresses(structInfo, writer);
            token.ThrowIfCancellationRequested();
        }

        // write string overloads
        if (!structInfo.StringOverloads.IsEmpty) {
            RenderStringOverloads(structInfo, writer);
            token.ThrowIfCancellationRequested();
        }

        // write span accessors for fixed size arrays
        if (!structInfo.FixedSizeArrays.IsEmpty) {
            RenderFixedSizeArrayAccessors(structInfo, writer);
            token.ThrowIfCancellationRequested();
        }

        // write closing struct hierarchy
        for (var i = 0; i < structInfo.Hierarchy.Length; i++) {
            writer.DecreaseIndent();
            writer.WriteLine("}");
        }

        return writer.ToString();
    }

    private static void RenderAddresses(StructInfo structInfo, IndentedTextWriter writer) {
        writer.WriteLine("public static class Addresses");
        using (writer.WriteBlock()) {
            foreach (MemberFunctionInfo mfi in structInfo.MemberFunctions) {
                writer.WriteLine(GetAddressString(structInfo, mfi.MethodInfo.Name, mfi.SignatureInfo));
            }
            foreach (StaticAddressInfo sai in structInfo.StaticAddresses) {
                writer.WriteLine(GetAddressString(structInfo, sai.MethodInfo.Name, sai.SignatureInfo));
            }
            if (structInfo.StaticVirtualTableSignature is not null) {
                writer.WriteLine(GetAddressString(structInfo, "StaticVirtualTable", structInfo.StaticVirtualTableSignature));
            }
        }
    }

    private static string GetAddressString(StructInfo structInfo, string signatureName, SignatureInfo signatureInfo) {
        string paddedSignature = signatureInfo.GetPaddedSignature();
        ImmutableArray<byte> relativeOffsets = signatureInfo.GetRelCallAndJumpAdjustedOffset();
        string offsets = "new byte[] {" + string.Join(", ", relativeOffsets) + "}";

        // get signature as ulong array
        IEnumerable<string> groupedSig = paddedSignature.Replace("??", "00").Split()
            .Select((x, i) => new { Index = i, Value = x })
            .GroupBy(x => x.Index / 8 * 3)
            .Select(x => x.Select(v => v.Value))
            .Select(x => "0x" + string.Join(string.Empty, x.Reverse()));

        string ulongArraySignature = "new ulong[] {" + string.Join(", ", groupedSig) + "}";

        // get signature mask as ulong array
        IEnumerable<string> groupedSigMask = paddedSignature.Split()
            .Select(s => s == "??" ? "00" : "FF")
            .Select((x, i) => new { Index = i, Value = x })
            .GroupBy(x => x.Index / 8 * 3)
            .Select(x => x.Select(v => v.Value))
            .Select(x => "0x" + string.Join(string.Empty, x.Reverse()));

        string ulongArrayMask = "new ulong[] {" + string.Join(", ", groupedSigMask) + "}";

        return $"""public static readonly global::InteropGenerator.Runtime.Address {signatureName} = new global::InteropGenerator.Runtime.Address("{structInfo.FullyQualifiedMetadataName}.{signatureName}", "{paddedSignature}", {offsets}, {ulongArraySignature}, {ulongArrayMask}, 0);""";
    }

    private static void RenderVirtualTable(StructInfo structInfo, IndentedTextWriter writer) {
        writer.WriteLine("[global::System.Runtime.InteropServices.StructLayoutAttribute(global::System.Runtime.InteropServices.LayoutKind.Explicit)]");
        writer.WriteLine($"public unsafe partial struct {structInfo.Name}VirtualTable");
        using (writer.WriteBlock()) {
            foreach (VirtualFunctionInfo vfi in structInfo.VirtualFunctions) {
                var functionPointerType = $"delegate* unmanaged <{structInfo.Name}*, {vfi.MethodInfo.GetParameterTypeStringWithTrailingType()}{vfi.MethodInfo.ReturnType}>";
                writer.WriteLine($"[global::System.Runtime.InteropServices.FieldOffsetAttribute({vfi.Index * 8})] public {functionPointerType} {vfi.MethodInfo.Name};");
            }
        }
        writer.WriteLine($"[global::System.Runtime.InteropServices.FieldOffsetAttribute(0)] public {structInfo.Name}VirtualTable* VirtualTable;");
        if (structInfo.StaticVirtualTableSignature is not null) {
            writer.WriteLine($"public static {structInfo.Name}VirtualTable* StaticVirtualTablePointer => ({structInfo.Name}VirtualTable*)Addresses.StaticVirtualTable.Value;");
        }
    }

    private static void RenderDelegateTypes(StructInfo structInfo, IndentedTextWriter writer) {
        writer.WriteLine($"public static partial class Delegates");
        using (writer.WriteBlock()) {
            foreach (MemberFunctionInfo memberFunctionInfo in structInfo.MemberFunctions) {
                RenderDelegateTypeForMethod(structInfo.Name, memberFunctionInfo.MethodInfo, writer);
            }
            foreach (VirtualFunctionInfo virtualFunctionInfo in structInfo.VirtualFunctions) {
                RenderDelegateTypeForMethod(structInfo.Name, virtualFunctionInfo.MethodInfo, writer);
            }
        }
    }

    private static void RenderDelegateTypeForMethod(string structType, MethodInfo methodInfo, IndentedTextWriter writer) {
        // special case marshalled bool return for assemblies without DisableRuntimeMarshalling
        if (methodInfo.ReturnType == "bool") {
            writer.WriteLine("[return:global::System.Runtime.InteropServices.MarshalAsAttribute(global::System.Runtime.InteropServices.UnmanagedType.U1)]");
        }
        string paramTypesAndNames;
        if (methodInfo.IsStatic) {
            paramTypesAndNames = methodInfo.GetParameterTypesAndNamesString();
        } else {
            paramTypesAndNames = $"{structType}* thisPtr";
            if (!methodInfo.Parameters.IsEmpty)
                paramTypesAndNames += $", {methodInfo.GetParameterTypesAndNamesString()}";
        }
        string methodModifiers = methodInfo.Modifiers.Replace(" partial", string.Empty).Replace(" static", string.Empty);
        writer.WriteLine($"{methodModifiers} delegate {methodInfo.ReturnType} {methodInfo.Name}({paramTypesAndNames});");
    }

    private static void RenderMemberFunctions(StructInfo structInfo, IndentedTextWriter writer) {
        // pointers to functions
        writer.WriteLine("public unsafe static class MemberFunctionPointers");
        using (writer.WriteBlock()) {
            foreach (MemberFunctionInfo mfi in structInfo.MemberFunctions) {
                // add struct type as first argument if method is not static
                string thisPtrType = mfi.MethodInfo.IsStatic ? string.Empty : $"{structInfo.Name}*, ";
                var functionPointerType = $"delegate* unmanaged <{thisPtrType}{mfi.MethodInfo.GetParameterTypeStringWithTrailingType()}{mfi.MethodInfo.ReturnType}>";
                writer.WriteLine($"public static {functionPointerType} {mfi.MethodInfo.Name} => ({functionPointerType}) {structInfo.Name}.Addresses.{mfi.MethodInfo.Name}.Value;");
            }
        }
        foreach (MemberFunctionInfo mfi in structInfo.MemberFunctions) {
            writer.WriteLine(mfi.MethodInfo.GetDeclarationString());
            using (writer.WriteBlock()) {
                writer.WriteLine($"if (MemberFunctionPointers.{mfi.MethodInfo.Name} is null)");
                using (writer.WriteBlock()) {
                    writer.WriteLine($"""InteropGenerator.Runtime.ThrowHelper.ThrowNullAddress("{structInfo.Name}.{mfi.MethodInfo.Name}", "{mfi.SignatureInfo.Signature}");""");
                }
                if (mfi.MethodInfo.IsStatic) {
                    writer.WriteLine($"{mfi.MethodInfo.GetReturnString()}MemberFunctionPointers.{mfi.MethodInfo.Name}({mfi.MethodInfo.GetParameterNamesString()});");
                } else {
                    var paramNames = string.Empty;
                    if (mfi.MethodInfo.Parameters.Any())
                        paramNames = ", " + mfi.MethodInfo.GetParameterNamesString();
                    writer.WriteLine($"{mfi.MethodInfo.GetReturnString()}MemberFunctionPointers.{mfi.MethodInfo.Name}(({structInfo.Name}*)global::System.Runtime.CompilerServices.Unsafe.AsPointer(ref this){paramNames});");
                }
            }
        }
    }

    private static void RenderVirtualFunctions(StructInfo structInfo, IndentedTextWriter writer) {
        foreach (VirtualFunctionInfo virtualFunctionInfo in structInfo.VirtualFunctions) {
            writer.WriteLine("[global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            var paramNames = string.Empty;
            if (virtualFunctionInfo.MethodInfo.Parameters.Any())
                paramNames = ", " + virtualFunctionInfo.MethodInfo.GetParameterNamesString();
            writer.WriteLine($"{virtualFunctionInfo.MethodInfo.GetDeclarationString()} => VirtualTable->{virtualFunctionInfo.MethodInfo.Name}(({structInfo.Name}*)global::System.Runtime.CompilerServices.Unsafe.AsPointer(ref this){paramNames});");
        }
    }

    private static void RenderStaticAddresses(StructInfo structInfo, IndentedTextWriter writer) {
        // pointers to static addresses
        writer.WriteLine("public unsafe static class StaticAddressPointers");
        using (writer.WriteBlock()) {
            foreach (StaticAddressInfo sai in structInfo.StaticAddresses) {
                string pointerText = sai.IsPointer ? "* p" : " ";
                string pointer = sai.IsPointer ? "*" : string.Empty;
                writer.WriteLine($"public static {sai.MethodInfo.ReturnType}{pointerText}p{sai.MethodInfo.Name} => ({sai.MethodInfo.ReturnType}{pointer}){structInfo.Name}.Addresses.{sai.MethodInfo.Name}.Value;");
            }
        }
        foreach (StaticAddressInfo sai in structInfo.StaticAddresses) {
            writer.WriteLine(sai.MethodInfo.GetDeclarationString());
            string extraPointerText = sai.IsPointer ? "p" : string.Empty;
            string pointerReturnText = sai.IsPointer ? "*" : string.Empty;

            using (writer.WriteBlock()) {
                writer.WriteLine($"if (StaticAddressPointers.{extraPointerText}p{sai.MethodInfo.Name} is null)");
                using (writer.WriteBlock()) {
                    writer.WriteLine($"""InteropGenerator.Runtime.ThrowHelper.ThrowNullAddress("{structInfo.Name}.{sai.MethodInfo.Name}", "{sai.SignatureInfo.Signature}");""");
                }
                writer.WriteLine($"return {pointerReturnText}StaticAddressPointers.{extraPointerText}p{sai.MethodInfo.Name};");
            }
        }
    }

    private static void RenderStringOverloads(StructInfo structInfo, IndentedTextWriter writer) {
        foreach (StringOverloadInfo stringOverloadInfo in structInfo.StringOverloads) {
            // collect valid replacement targets
            ImmutableArray<string> paramsToOverload = [.. stringOverloadInfo.MethodInfo.Parameters.Where(p => p.Type == "byte*" && !stringOverloadInfo.IgnoredParameters.Contains(p.Name)).Select(p => p.Name)];

            // when calling the original function we need the param names, but use "Ptr" for the arguments that have been converted
            string paramNames = stringOverloadInfo.MethodInfo.GetParameterNamesStringForStringOverload(paramsToOverload);

            // "string" overload
            writer.WriteLine(stringOverloadInfo.MethodInfo.GetDeclarationStringForStringOverload("string", paramsToOverload));
            using (writer.WriteBlock()) {
                foreach (string overloadParamName in paramsToOverload) {
                    // allocate space for string, supporting UTF8 characters
                    var strLenName = $"{overloadParamName}UTF8StrLen";

                    writer.WriteLine($"int {strLenName} = global::System.Text.Encoding.UTF8.GetByteCount({overloadParamName});");
                    writer.WriteLine($"Span<byte> {overloadParamName}Bytes = {strLenName} <= 512 ? stackalloc byte[{strLenName} + 1] : new byte[{strLenName} + 1];");
                    writer.WriteLine($"global::System.Text.Encoding.UTF8.GetBytes({overloadParamName}, {overloadParamName}Bytes);");
                    writer.WriteLine($"{overloadParamName}Bytes[{strLenName}] = 0;");
                }

                foreach (string overloadParamName in paramsToOverload) {
                    writer.WriteLine($"fixed (byte* {overloadParamName}Ptr = {overloadParamName}Bytes)");
                    writer.WriteLine("{");
                    writer.IncreaseIndent();
                }

                writer.WriteLine($"{stringOverloadInfo.MethodInfo.GetReturnString()}{stringOverloadInfo.MethodInfo.Name}({paramNames});");

                foreach (string _ in paramsToOverload) {
                    writer.DecreaseIndent();
                    writer.WriteLine("}");
                }
            }
            // "ReadOnlySpan<byte>" overload
            writer.WriteLine(stringOverloadInfo.MethodInfo.GetDeclarationStringForStringOverload("ReadOnlySpan<byte>", paramsToOverload));
            using (writer.WriteBlock()) {
                foreach (string overloadParamName in paramsToOverload) {
                    writer.WriteLine($"fixed (byte* {overloadParamName}Ptr = {overloadParamName})");
                    writer.WriteLine("{");
                    writer.IncreaseIndent();
                }

                writer.WriteLine($"{stringOverloadInfo.MethodInfo.GetReturnString()}{stringOverloadInfo.MethodInfo.Name}({paramNames});");

                foreach (string _ in paramsToOverload) {
                    writer.DecreaseIndent();
                    writer.WriteLine("}");
                }
            }
        }
    }

    private static void RenderFixedSizeArrayAccessors(StructInfo structInfo, IndentedTextWriter writer) {
        foreach (FixedSizeArrayInfo fixedSizeArrayInfo in structInfo.FixedSizeArrays) {
            writer.WriteLine($"""/// <inheritdoc cref="{fixedSizeArrayInfo.FieldName}" />""");
            // [UnscopedRef] public Span<T> FieldName => _fieldName;
            writer.WriteLine($"[global::System.Diagnostics.CodeAnalysis.UnscopedRefAttribute] public Span<{fixedSizeArrayInfo.Type}> {fixedSizeArrayInfo.GetPublicFieldName()} => {fixedSizeArrayInfo.FieldName};");
            if (fixedSizeArrayInfo.IsString) {
                writer.WriteLine($"""/// <inheritdoc cref="{fixedSizeArrayInfo.FieldName}" />""");
                writer.WriteLine($"public string {fixedSizeArrayInfo.GetPublicFieldName()}String");
                using (writer.WriteBlock()) {
                    if (fixedSizeArrayInfo.Type == "byte") {
                        // Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)Unsafe.AsPointer(ref _field[0])))
                        writer.WriteLine($"get => global::System.Text.Encoding.UTF8.GetString(global::System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)global::System.Runtime.CompilerServices.Unsafe.AsPointer(ref {fixedSizeArrayInfo.FieldName}[0])));");
                        writer.WriteLine("set");
                        using (writer.WriteBlock()) {
                            writer.WriteLine($"if (global::System.Text.Encoding.UTF8.GetByteCount(value) > {fixedSizeArrayInfo.Size} - 1)");
                            using (writer.WriteBlock()) {
                                writer.WriteLine($"""InteropGenerator.Runtime.ThrowHelper.ThrowStringSizeTooLarge("{fixedSizeArrayInfo.GetPublicFieldName()}String", {fixedSizeArrayInfo.Size});""");
                            }
                            writer.WriteLine($"global::System.Text.Encoding.UTF8.GetBytes(value.AsSpan(), {fixedSizeArrayInfo.FieldName});");
                            writer.WriteLine($"{fixedSizeArrayInfo.FieldName}[{fixedSizeArrayInfo.Size - 1}] = 0;");
                        }
                    } else if (fixedSizeArrayInfo.Type == "char") {
                        writer.WriteLine($"get => new string({fixedSizeArrayInfo.FieldName});");
                        writer.WriteLine("set");
                        using (writer.WriteBlock()) {
                            writer.WriteLine($"if (value.Length > {fixedSizeArrayInfo.Size} - 1)");
                            using (writer.WriteBlock()) {
                                writer.WriteLine($"""InteropGenerator.Runtime.ThrowHelper.ThrowStringSizeTooLarge("{fixedSizeArrayInfo.GetPublicFieldName()}String", {fixedSizeArrayInfo.Size});""");
                            }
                            writer.WriteLine($"value.CopyTo({fixedSizeArrayInfo.FieldName});");
                            writer.WriteLine($"{fixedSizeArrayInfo.FieldName}[{fixedSizeArrayInfo.Size - 1}] = '\\0';");
                        }
                    }
                }
            }
        }
    }

    private static string RenderResolverInitializer(ImmutableArray<StructInfo> structInfos, string generatorNamespace) {
        using IndentedTextWriter writer = new();
        // write file header
        writer.WriteLine("// <auto-generated/>");
        writer.WriteLine($"namespace {generatorNamespace};");
        writer.WriteLine("public static class Addresses");
        using (writer.WriteBlock()) {
            writer.WriteLine("public static void Register()");
            using (writer.WriteBlock()) {
                foreach (StructInfo sInfo in structInfos) {
                    // member function addresses
                    foreach (MemberFunctionInfo mfi in sInfo.MemberFunctions) {
                        writer.WriteLine(GetAddToResolverString(sInfo, mfi.MethodInfo.Name));
                    }
                    // static addresses
                    foreach (StaticAddressInfo sai in sInfo.StaticAddresses) {
                        writer.WriteLine(GetAddToResolverString(sInfo, sai.MethodInfo.Name));
                    }
                    // static virtual table
                    if (sInfo.StaticVirtualTableSignature is not null) {
                        writer.WriteLine(GetAddToResolverString(sInfo, "StaticVirtualTable"));
                    }
                }
            }
            writer.WriteLine("public static void Unregister()");
            using (writer.WriteBlock()) {
                foreach (StructInfo sInfo in structInfos) {
                    // member function addresses
                    foreach (MemberFunctionInfo mfi in sInfo.MemberFunctions) {
                        writer.WriteLine(GetRemoveFromResolverString(sInfo, mfi.MethodInfo.Name));
                    }
                    // static addresses
                    foreach (StaticAddressInfo sai in sInfo.StaticAddresses) {
                        writer.WriteLine(GetRemoveFromResolverString(sInfo, sai.MethodInfo.Name));
                    }
                    // static virtual table
                    if (sInfo.StaticVirtualTableSignature is not null) {
                        writer.WriteLine(GetRemoveFromResolverString(sInfo, "StaticVirtualTable"));
                    }
                }
            }
        }

        return writer.ToString();
    }

    private static string GetAddToResolverString(StructInfo structInfo, string signatureName) {
        string namespaceString = string.IsNullOrEmpty(structInfo.Namespace) ? string.Empty : structInfo.Namespace + ".";
        var fullTypeName = $"global::{namespaceString}{string.Join(".", structInfo.Hierarchy.Reverse())}";
        return $"InteropGenerator.Runtime.Resolver.GetInstance.RegisterAddress({fullTypeName}.Addresses.{signatureName});";
    }

    private static string GetRemoveFromResolverString(StructInfo structInfo, string signatureName) {
        string namespaceString = string.IsNullOrEmpty(structInfo.Namespace) ? string.Empty : structInfo.Namespace + ".";
        var fullTypeName = $"global::{namespaceString}{string.Join(".", structInfo.Hierarchy.Reverse())}";
        return $"InteropGenerator.Runtime.Resolver.GetInstance.UnregisterAddress({fullTypeName}.Addresses.{signatureName});";
    }

    private static string RenderFixedArrayTypes(ImmutableArray<StructInfo> structInfos, string generatorNamespace) {
        using IndentedTextWriter writer = new();
        HashSet<int> generatedSizes = [];

        writer.WriteLine("// <auto-generated/>");
        writer.WriteLine($"namespace {generatorNamespace};");
        foreach (StructInfo structInfo in structInfos) {
            foreach (FixedSizeArrayInfo fixedSizeArrayInfo in structInfo.FixedSizeArrays) {
                if (generatedSizes.Contains(fixedSizeArrayInfo.Size))
                    continue;

                writer.WriteLine($"[global::System.Runtime.CompilerServices.InlineArrayAttribute({fixedSizeArrayInfo.Size})]");
                writer.WriteLine($"public struct FixedSizeArray{fixedSizeArrayInfo.Size}<T> where T : unmanaged");
                using (writer.WriteBlock()) {
                    writer.WriteLine("private T _element0;");
                }
                generatedSizes.Add(fixedSizeArrayInfo.Size);
            }
        }

        return writer.ToString();
    }
}
