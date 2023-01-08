using Microsoft.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SourceGeneratorT4
{
    [Generator(LanguageNames.CSharp)]
    public class IncrementalGenerator : IIncrementalGenerator
    {
        private static Assembly s_coreAssembly;
        private static Type s_incrementalGeneratorType;
        private IIncrementalGenerator _internal;

        static IncrementalGenerator()
        {
            string name;

            if (RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework"))
            {
                name = "SourceGeneratorT4Core.net472.dll";
            }
            else
            {
                name = "SourceGeneratorT4Core.net6.dll";
            }

            using var resourceStream = typeof(IncrementalGenerator).Assembly.GetManifestResourceStream(name);

            var rawAssembly = new byte[resourceStream.Length];
            var readCount = 0;
            while (readCount < resourceStream.Length)
                readCount += resourceStream.Read(rawAssembly, readCount, (int)resourceStream.Length - readCount);

            s_coreAssembly = Assembly.Load(rawAssembly);

            s_incrementalGeneratorType = s_coreAssembly.GetType("SourceGeneratorT4Core.IncrementalGenerator");
        }

        public IncrementalGenerator()
        {
            _internal = (IIncrementalGenerator)Activator.CreateInstance(s_incrementalGeneratorType);
        }

        public void Initialize(IncrementalGeneratorInitializationContext context) => _internal.Initialize(context);
    }
}
