using CardCrafter.Core.Compiler;
using CardCrafter.Core.Tests;
using CardCrafter.Core.Text;
var p = Samples.DragonCommander();
Console.WriteLine(ScriptCompiler.Compile(p).Lua);
Console.WriteLine("---- TEXT");
Console.WriteLine(EffectTextGenerator.Generate(p, Samples.Pack(p)));
Console.WriteLine(EffectTextGenerator.Generate(Samples.PotOfGreedClone()));
