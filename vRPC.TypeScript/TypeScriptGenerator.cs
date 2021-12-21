using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using vRPC.core;
using Vigor.Functional;
using Vigor.Functional.Option;

namespace vRPC.TypeScript
{
    public class TypeScriptGenerator: IGenerator
    {
        public IEnumerable<(string fileName, string content)> Generate(Type service)
        {
            var defaultFactory = new TypeFactory(new Dictionary<Type, TsType>());
            var (methods, newTypes) = service.GetMethods().Aggregate((methods: Enumerable.Empty<MethodDeclaration>(), types: new HashSet<Type>()), (tuple, info) =>
            {
                var (newMethod, newTypes) = GenerateMethod(info, defaultFactory);
                return (tuple.methods.Append(newMethod), tuple.types.Concat(newTypes).ToHashSet());
            });
            
            var argumentMap = new Constant($"{service.Name}_arguments", $"{{{CommaJoin(methods.Select(ArgumentEntry))}}}");

            var newTsTypes = newTypes.Aggregate((codes: Enumerable.Empty<ICode>(), factory: defaultFactory), (tuple, type) =>
            {
                var res = GenerateNewType(type, tuple.factory);
                return (tuple.codes.Concat(res.code), res.newFactory);
            });

            var @interface = new Interface(service.Name, methods, Enumerable.Empty<Field>());

            var rttiInfo = new TypedConstant($"{service.Name}_methods", new TsType($"Array<keyof {@interface.Name}>"),
                $"[{string.Join(",", methods.Select(m => $"'{m.Name}'"))}]");

            var file = new File(Enumerable.Empty<Import>(), newTsTypes.codes.Concat(new ICode[]{@interface, rttiInfo, argumentMap }), new Exports(new INamed[]{@interface, rttiInfo, argumentMap }));
            var writer = new CodeWriter();
            file.Generate(writer);
            string code = writer.GetCode();
            return new[] { ($"{service.Name}_vrpc.ts", code) };
        }

        private static (IEnumerable<ICode> code, TypeFactory newFactory) GenerateNewType(Type type, TypeFactory currentFactory)
        {
            if (currentFactory.IsKnown(type))
                return (Enumerable.Empty<ICode>(), currentFactory);

            var fields = type.GetProperties().Select(f => new Field(f.Name, currentFactory.Map(f.PropertyType)));
            var (subCodes, subFactory) = type.GetProperties().Select(f => f.PropertyType)
                .Aggregate((codes: Enumerable.Empty<ICode>(), currentFactory), (tuple, subType) =>
                {
                    var (newCodes, newFactory) = GenerateNewType(subType, tuple.currentFactory);
                    return (tuple.codes.Concat(newCodes), newFactory);
                });
            var @interface = new Interface(type.Name, Enumerable.Empty<MethodDeclaration>(), fields);
            return (subCodes.Append(@interface), subFactory.Append(type, currentFactory.Map(type)));
        }

        private string ArgumentEntry(MethodDeclaration method) => $"{Quote(method.Name)}: [{CommaJoin(method.args.Select(a => Quote(a.Name)))}]";

        private static string CommaJoin(IEnumerable<string> args) => string.Join(", ", args);
        private static string Quote(string arg) => $"\"{arg}\"";

        private (MethodDeclaration method, IEnumerable<Type> unknowTypes) GenerateMethod(MethodInfo source, TypeFactory typeFactory)
        {
            var arguments = source.GetParameters().Select(p => new Argument(p.Name!, typeFactory.Map(p.ParameterType))).ToList();
            var declaration = new MethodDeclaration(source.Name, arguments, typeFactory.Map(source.ReturnType));
            var unknownTypes = source.GetParameters().Append(source.ReturnParameter).Distinct().Where(p => !typeFactory.IsKnown(p.ParameterType)).Select(p => p.ParameterType);
            return (declaration, unknownTypes);
        }

    }

    class TypeFactory
    {
        private readonly Dictionary<Type, TsType> _additionalTypes;

        private Dictionary<Type, TsType> builtins = new()
        {
            {typeof(int), new TsType("number")},
            {typeof(long), new TsType("number") },
            {typeof(string), new TsType("string") },
        };

        public TypeFactory(): this(new Dictionary<Type, TsType>())
        {
        }

        public TypeFactory(Dictionary<Type, TsType> additionalTypes)
        {
            _additionalTypes = additionalTypes;
        }

        public TypeFactory Append(Type type, TsType tsType)
        {
            return this.Extend(new Dictionary<Type, TsType>() { {type, tsType} });
        }

        public TypeFactory Extend(IDictionary<Type, TsType> newTypes) => 
            new(_additionalTypes.Concat(newTypes).ToDictionary(pair => pair.Key, pair => pair.Value));

        public bool IsKnown(Type type) => builtins.ContainsKey(type) || _additionalTypes.ContainsKey(type);
        public TsType Map(Type type) => builtins.GetOrNone(type).Match(tsType => tsType, () => new TsType(type.Name));
    }

    interface INamed
    {
        public string Name { get; }
    }
    interface ICode
    {
        void Generate(CodeWriter writer);
    }
    record File(IEnumerable<Import> Imports, IEnumerable<ICode> Contents, Exports Exports): ICode
    {
        public void Generate(CodeWriter writer)
        {
            Imports.ForEach(i => i.Generate(writer));
            Contents.ForEach(i => i.Generate(writer));
            Exports.Generate(writer);
        }
    }

    record TsType(string Name): INamed;
    record Import(string Name, string From): ICode
    {
        public void Generate(CodeWriter writer)
        {
            writer.Line($"import * as {Name} from \"{From}\";");
        }
    }

    record Interface(string Name, IEnumerable<MethodDeclaration> Methods, IEnumerable<Field> Fields) : ICode, INamed
    {
        public void Generate(CodeWriter writer)
        {
            writer.Write($"interface {Name}");
            
            writer.Block(codeWriter =>
            {
                void WriteStuff(IEnumerable<ICode> blocks) => blocks.ForEach(b =>
                {
                    b.Generate(codeWriter);
                    codeWriter.NewLine();
                });
                
                WriteStuff(Methods);
                WriteStuff(Fields);
            });
        }
    }

    record Field(string Name, TsType Type) : ICode
    {
        public void Generate(CodeWriter writer)
        {
            writer.Write($"{Name}: {Type.Name};");
        }
    }

    record Class(string Name, IEnumerable<Method> Methods): ICode, INamed
    {
        public void Generate(CodeWriter writer)
        {
            writer.Write($"class {Name}");
            writer.Block(codeWriter => Methods.ForEach(m =>
            {
                m.Generate(codeWriter);
                codeWriter.NewLine();
            }));
        }
    }

    record Exports(IEnumerable<INamed> Entities) : ICode
    {
        public void Generate(CodeWriter writer)
        {
            if(Entities.Any())
                writer.Line($"export {{{string.Join(", ", Entities.Select(e => e.Name))}}}");
        }
    }

    internal record Argument(string Name, TsType Type);

    internal record MethodDeclaration(string Name, IList<Argument> args, TsType ReturnType) : ICode, INamed
    {
        public void GeneratePartial(CodeWriter writer)
        {
            var argList = string.Join(",", args.Select(a => $"{a.Name}: {a.Type.Name}"));
            writer.Write($"{Name}({argList}): {ReturnType.Name}");
        }

        public void Generate(CodeWriter writer)
        {
            GeneratePartial(writer);
            writer.Write(";");
        }
    }

    record Constant(string Name, string Expression): ICode, INamed
    {
        public void Generate(CodeWriter writer)
        {
            writer.Line($"const {Name} = {Expression};");
        }
    }

    record TypedConstant(string Name, TsType Type, string Expression) : ICode, INamed
    {
        public void Generate(CodeWriter writer)
        {
            writer.Line($"const {Name} : {Type.Name} = {Expression};");
        }
    }

    internal record Method(MethodDeclaration Declaration, string Body) : ICode
    {
        public void Generate(CodeWriter writer)
        {
            Declaration.GeneratePartial(writer);
            writer.Block(()=>Body);
        }
    }

    class CodeWriter
    {
        private StringBuilder _code = new();
        private int _indent = 0;

        public void Line(string content)
        {
            Write(content);
            NewLine();
        }
        public void Write(string content)
        {
            _code.Append(content);
        }

        public void Block(Action<CodeWriter> blockGeneration)
        {
            Line("{");
            _indent++;
            WriteIndent();
            blockGeneration(this);
            _indent--;
            Line("}");
        }

        private void WriteIndent()
        {
            Write(new string(' ', _indent*4));
        }

        public void Block(Func<string> blockGeneration)
        {
            Block(writer => writer.Write(blockGeneration()));
        }

        public void NewLine() => Write("\r\n");

        public string GetCode() => _code.ToString();
    }
};