using System;
using System.Reflection;

namespace UCHGamePerformance
{
    internal static class SignaturePolicy
    {
        // Avoid MethodBase.ToString() and assembly-qualified generic FullName: formatting and
        // core-library assembly identity differ between the audit CLR and Unity's Mono runtime.
        internal static string Read(MethodBase method)
        {
            string result = Name(method.DeclaringType) + "::" + method.Name + "(";
            ParameterInfo[] arguments = method.GetParameters();
            for (int i = 0; i < arguments.Length; i++) result += (i == 0 ? "" : ",") + Name(arguments[i].ParameterType);
            MethodInfo info = method as MethodInfo;
            return result + "):" + (info == null ? "constructor" : Name(info.ReturnType)) + (method.IsStatic ? ":static" : ":instance");
        }

        private static string Name(Type type)
        {
            if (type.IsByRef) return Name(type.GetElementType()) + "&";
            if (type.IsArray) return Name(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (!type.IsGenericType) return type.FullName ?? type.Name;
            string result = type.GetGenericTypeDefinition().FullName + "<";
            Type[] arguments = type.GetGenericArguments();
            for (int i = 0; i < arguments.Length; i++) result += (i == 0 ? "" : ",") + Name(arguments[i]);
            return result + ">";
        }
    }
}
