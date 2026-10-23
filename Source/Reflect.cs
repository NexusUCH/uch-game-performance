using System;
using System.Reflection;

namespace UCHGamePerformance
{
    // Plain reflection also permits metadata/transpiler tests without initializing Harmony's
    // runtime detour machinery on a different CLR. This is only used at installation time.
    internal static class Reflect
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        internal static FieldInfo Field(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            { FieldInfo field = current.GetField(name, Flags); if (field != null) return field; }
            return null;
        }
        internal static MethodInfo DeclaredMethod(Type type, string name) { return DeclaredMethod(type, name, null); }
        internal static MethodInfo DeclaredMethod(Type type, string name, Type[] arguments)
        {
            if (arguments != null) return type.GetMethod(name, Flags, null, arguments, null);
            MethodInfo result = null;
            foreach (MethodInfo method in type.GetMethods(Flags))
                if (method.Name == name)
                { if (result != null) throw new AmbiguousMatchException(type.FullName + "." + name); result = method; }
            return result;
        }
        internal static MethodInfo Method(Type type, string name) { return Method(type, name, null); }
        internal static MethodInfo Method(Type type, string name, Type[] arguments)
        {
            for (Type current = type; current != null; current = current.BaseType)
            { MethodInfo method = DeclaredMethod(current, name, arguments); if (method != null) return method; }
            return null;
        }
        internal static ConstructorInfo Constructor(Type type, Type[] arguments)
        { return type.GetConstructor(Flags, null, arguments, null); }
    }
}
