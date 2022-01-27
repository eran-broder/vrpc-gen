// See https://aka.ms/new-console-template for more information

using System.Reflection;
using Vigor.Functional;
using vRPC.Common;
using vRPC.core;

Console.WriteLine("Hello, World!");
PerformGeneration(@"C:\Users\broder\source\repos\Vigor\Vigor.Rpc\bin\Debug\net6.0-windows\Vigor.Rpc.dll", 
                   "Vigor.Rpc.IRemoteInspector", 
                  @"C:\vim\vrpc\src", 
                   "vRPC.TypeScript.TypeScriptGenerator, vRPC.TypeScript");

PerformGeneration(@"C:\Users\broder\source\repos\Vigor\Vigor.Inspector.Interactive\bin\Debug\net6.0-windows\Vigor.Inspector.Interactive.dll",
    "Vigor.Inspector.Interactive.RemoteElementHighlighter",
    @"C:\vim\vrpc\src",
    "vRPC.TypeScript.TypeScriptGenerator, vRPC.TypeScript");

void PerformGeneration(string pathOfInput, string nameOfClass, string pathOfOutput, string flavor)
{
    var type = GetTypeToReflect(pathOfInput, nameOfClass);
    IGenerator generator = GetGenerator(flavor);
    Assertions.NotNull(generator, $"Cannot find generator with the name ${flavor}");
    var result = generator.Generate(type);
    result.ForEach(res =>
    {
        var dest = Path.Join(pathOfOutput, res.fileName);
        File.WriteAllText(dest, res.content);
    });
}

Type GetTypeToReflect(string pathOfFile, string nameOfClass)
{
    Assertions.Assert(File.Exists(pathOfFile), $"Cannot find file : {pathOfFile}");
    var assembly = Assembly.LoadFrom(pathOfFile);
    var type = assembly.GetType(nameOfClass);
    Assertions.NotNull(type, $"Cannot find type {nameOfClass} within assembly {assembly.FullName} at {pathOfFile}");
    return type!;
}

//TODO: no need to have the type loaded into memory. load it dynamiclly
IGenerator GetGenerator(string flavorName)
{
    var type = Type.GetType(flavorName);
    Assertions.NotNull(type, $"Cannot find any type matching {nameof(IGenerator)} within assembly");
    var instance = Activator.CreateInstance(type!);
    return (IGenerator)instance!;
}
