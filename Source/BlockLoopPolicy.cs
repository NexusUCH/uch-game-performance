using System;
using System.Reflection;

namespace UCHGamePerformance
{
    public static class BlockLoopPolicy
    {
        public static bool InheritsEmptyAct(Type runtimeType, Type baseType, MethodInfo verifiedBaseAct)
        {
            if (runtimeType == null || baseType == null || verifiedBaseAct == null || !baseType.IsAssignableFrom(runtimeType)) return false;
            MethodInfo act = runtimeType.GetMethod("Act", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new Type[] { typeof(float) }, null);
            return act != null && act.DeclaringType == baseType && act.Module == verifiedBaseAct.Module && act.MetadataToken == verifiedBaseAct.MetadataToken;
        }
    }
}
