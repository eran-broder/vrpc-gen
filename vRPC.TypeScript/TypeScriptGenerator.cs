using System.Collections.ObjectModel;
using System.Reflection;
using System.Xml;
using vRPC.core;
using Vigor.Functional;
using Vigor.Functional.Option;

namespace vRPC.TypeScript
{
    public class TypeScriptGenerator: IGenerator
    {
        public IEnumerable<(string fileName, string content)> Generate(Type service)
        {
            var (methods, newTypesFromMethods) = ExtractMethods(service);
            var (events, newTypesFromEvents) = ExtractEvents(service);
            var allNewTypes = newTypesFromMethods.Union(newTypesFromEvents).ToList();
            var argumentMap = new Constant($"{service.Name}_arguments", $"{{{U.CommaJoin(methods.Select(ArgumentEntry))}}}");

            var (codes, finalFactory) = GenerateNewTypes(allNewTypes, TypeFactory.Builtins());

            var promisifiedMethods = methods.Select(m => m with { ReturnType = m.ReturnType.Promisify() });
            var @interface = new Interface(service.Name, promisifiedMethods.Cast<ICode>().Concat(events));

            var rttiInfo = new TypedConstant($"{service.Name}_methods", new TsType($"Array<coreTypes.WithoutEvents<{@interface.Name}>>"),
                $"[{string.Join(",", methods.Where(m => m.Name != "on").Select(m => $"'{m.Name}'"))}]");

            var imports = new[] { new Import("coreTypes", "./rpcCoreTypes") };
            var file = new File(imports.Concat(codes).Concat(new ICode[]{@interface, rttiInfo, argumentMap }), new Exports(new INamed[]{@interface, rttiInfo, argumentMap}.Concat(finalFactory.NonBuiltins)));
            var writer = new CodeWriter();
            file.Generate(writer);
            string code = writer.GetCode();
            return new[] { ($"{service.Name}_vrpc.ts", code) };
        }

        private static (IEnumerable<Event> methods, HashSet<Type> newTypes) ExtractEvents(Type @interface)
        {
            return @interface.GetEvents().Aggregate((events: Enumerable.Empty<Event>(), types: new HashSet<Type>()), (tuple, info) =>
            {
                var (newMethod, newTypes) = GenerateEvent(info, TypeFactory.Builtins());
                return (tuple.events.Append(newMethod), tuple.types.Concat(newTypes).ToHashSet());
            });
        }

        private static (IEnumerable<MethodDeclaration> methods, HashSet<Type> newTypes) ExtractMethods(Type @interface)
        {
            return @interface.GetMethods().Where(m => !m.IsSpecialName).Aggregate((methods: Enumerable.Empty<MethodDeclaration>(), types: new HashSet<Type>()), (tuple, info) =>
            {
                var (newMethod, newTypes) = GenerateMethod(info, TypeFactory.Builtins());
                return (tuple.methods.Append(newMethod).ToList(), tuple.types.Concat(newTypes).ToHashSet());
            });
        }

        

        private static (IEnumerable<ICode> code, TypeFactory newFactory) GenerateNewTypes(IEnumerable<Type> types, TypeFactory seedFactory)
        {
            types = types.ToList();
            var unknownTypes = types.SelectMany(seedFactory.UnknownTypes).Distinct().ToList();
            return unknownTypes.Aggregate(
                (codes: Enumerable.Empty<ICode>(), currentFactory: seedFactory), (tuple, unknownType) =>
                {
                    var (code, subTypes, updatedFactory) = GenerateNewType(unknownType, tuple.currentFactory);
                    var (subCodes, subFactory) = GenerateNewTypes(subTypes, updatedFactory);
                    return (tuple.codes.Concat(subCodes).Append(code), subFactory);
                });
        }

        private static (@ICode code, IEnumerable<Type> subTypes, TypeFactory updatedFactory) GenerateNewType(Type unknownType, TypeFactory factory)
        {
            var codeName = factory.Map(unknownType).Name;
            var updatedFactory = factory.Append(unknownType, new TsType(codeName));

            if (unknownType.IsClass || unknownType.IsInterface)
            {
                var fields = unknownType.GetProperties().Select(f =>
                    new Field(U.LowerFirst(f.Name), factory.Map(f.PropertyType)));
                var @interface = new Interface(codeName, fields);
                var subTypes = unknownType.GetProperties().Select(f => f.PropertyType);
                return (@interface, subTypes, updatedFactory);
            }
            else if (unknownType.IsEnum)
            {
                var @enum = new Enum(codeName, System.Enum.GetNames(unknownType));
                return (@enum, Enumerable.Empty<Type>(), updatedFactory);
            }
            else
            {
                throw new Exception($"Type [{unknownType.FullName}] is not a class/Interface/Enum.");
            }
            
        }

        private string ArgumentEntry(MethodDeclaration method) => $"{U.Quote(method.Name)}: [{U.CommaJoin(method.args.Select(a => U.Quote(a.Name)))}]";

        private static (MethodDeclaration method, IEnumerable<Type> unknowTypes) GenerateMethod(MethodInfo source, TypeFactory typeFactory)
        {
            var arguments = source.GetParameters().Select(p => new Argument(p.Name!, typeFactory.Map(p.ParameterType))).ToList();
            var declaration = new MethodDeclaration(source.Name, arguments, typeFactory.Map(source.ReturnType));
            var allAvailableTypes = source.GetParameters().Append(source.ReturnParameter).Select(p => p.ParameterType);
            var unknownTypes = allAvailableTypes.SelectMany(typeFactory.UnknownTypes).Distinct();
            return (declaration, unknownTypes);
        }

        private static (Event method, IEnumerable<Type> unknowTypes) GenerateEvent(EventInfo eventInfo, TypeFactory typeFactory)
        {
            var invokerMethod = eventInfo.EventHandlerType!.GetMethod("Invoke")!;
            var (eventAsMethod, unknownTypes) = GenerateMethod(invokerMethod, typeFactory);
            var tsEvent = new Event(eventInfo.Name, eventAsMethod.args);
            return (tsEvent, unknownTypes);
        }
    }

    static class U
    {
        public static string CommaJoin(IEnumerable<string> args) => string.Join(", ", args);
        public static string Quote(string arg) => $"\"{arg}\"";

        public static string LowerFirst(string argName) => $"{argName[0].ToString().ToLower()}{argName[1..]}";
    }

    class TypeFactory
    {
        private readonly Dictionary<Type, TsType> _knownTypes;

        private static Dictionary<Type, TsType> builtins = new()
        {
            {typeof(int), new TsType("number")},
            {typeof(long), new TsType("number") },
            {typeof(string), new TsType("string") },
            {typeof(void), new TsType("void") },
            {typeof(List<>), new TsType("Array")},
            {typeof(object), new TsType("any")}
        };


        private TypeFactory(): this(new Dictionary<Type, TsType>())
        {
        }

        public static TypeFactory Builtins() => new(builtins);

        public TypeFactory(Dictionary<Type, TsType> additionalTypes)
        {
            _knownTypes = additionalTypes;
        }

        public TypeFactory Append(Type type, TsType tsType)
        {
            return this.Extend(new Dictionary<Type, TsType>() { {type, tsType} });
        }

        public TypeFactory Extend(IDictionary<Type, TsType> newTypes) => 
            new(_knownTypes.Concat(newTypes).ToDictionary(pair => pair.Key, pair => pair.Value));
        public IEnumerable<Type> UnknownTypes(Type type)
        {
            if (type.IsGenericType && ! type.GetGenericArguments().First().IsGenericTypeParameter)
                return UnknownTypes(type.GetGenericTypeDefinition())
                    .Concat(UnknownTypes(type.GetGenericArguments().First()));
            else
            {
                return _knownTypes.ContainsKey(type) ? Enumerable.Empty<Type>() : new[] { type };
            }
                
        }

        public IEnumerable<TsType> NonBuiltins => _knownTypes.Where(t => !builtins.ContainsKey(t.Key)).AsDict().Values;
        public TsType Map(Type type) => builtins.GetOrNone(type).Match(tsType => tsType, () => MapNonBuiltin(type));


        private TsType MapGeneric(Type type)
        {
            var baseType = type.GetGenericTypeDefinition();
            var genericArgument = type.GenericTypeArguments.First();
            var res = Map(baseType).AsGeneric(Map(genericArgument));
            return res;
        }

        private TsType MapNonBuiltin(Type type)
        {
            if(type.IsGenericType)
            {
                return MapGeneric(type);
            }
            else
            {
                return new TsType(type.Name);
            }
        }
    }

    interface INamed
    {
        public string Name { get; }
    }
    interface ICode
    {
        void Generate(CodeWriter writer);
    }
    record File(IEnumerable<ICode> Contents, Exports Exports): ICode
    {
        public void Generate(CodeWriter writer)
        {
            Contents.ForEach(i => i.Generate(writer));
            Exports.Generate(writer);
        }
    }

    record TsType(string Name): INamed;
    static class TsTypeExtensions
    {
        public static TsType Promisify(this TsType type) => new TsType($"Promise<{type.Name}>");
        public static TsType AsGeneric(this TsType type, TsType argument) => new TsType($"{type.Name}<{argument.Name}>");
    }
    record Import(string Name, string From): ICode
    {
        public void Generate(CodeWriter writer)
        {
            writer.Line($"import * as {Name} from \"{From}\";");
        }
    }

    record Interface(string Name, IEnumerable<ICode> Segments) : ICode, INamed
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
                
                WriteStuff(Segments);
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

    internal record Event(string Name, IList<Argument> args) : ICode
    {
        public void Generate(CodeWriter writer)
        {
            //on(eventName: "click", callback: (a: number, b: number)=>void): void;
            
            writer.Write($"on(eventName:{U.Quote(Name)}, callback: ({U.CommaJoin(args.Select(a => $"{a.Name}: {a.Type.Name}"))}) => void): void;");
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

    internal record Enum(string Name, IEnumerable<string> EnumFields) : ICode, INamed
    {
        public void Generate(CodeWriter writer)
        {
            writer.Write($"enum {Name}");
            writer.Block((w) =>
            {
                EnumFields.ForEach(f => w.Line($"{f},"));
            });
        }
    }
};