// netstandard2.0 缺少 C# 9/10 引入的这几个编译器支撑类型，
// 这里补一份内部实现，使 required / init / record 等特性可正常编译。
namespace System.Runtime.CompilerServices
{
    internal sealed class IsExternalInit { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;
        public string FeatureName { get; }
        public bool IsOptional { get; set; }
    }
}
