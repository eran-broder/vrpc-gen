using System.Text;

namespace vRPC.TypeScript;

class CodeWriter
{
    private StringBuilder _code = new();
    private int _indent = 0;

    public void Line(string content)
    {
        Write(content);
        NewLine();
    }
    public void Write(string content) => _code.Append(AddIndentation(content));
    private string AddIndentation(string content) => content.Replace("\n", CurrentIndent);

    public void Block(Action<CodeWriter> blockGeneration)
    {
        Line("{");
        _indent++;
        Write(CurrentIndent);
        blockGeneration(this);
        _indent--;
        RemoveNewIndent();
        Line("}");
    }

    private void RemoveNewIndent()
    {
        if (_code.ToString().EndsWith(SingleIndent))
        {
            var indentLength = SingleIndent.Length;
            _code.Remove(_code.Length - indentLength, indentLength);
        }
    }

    private const int IndentNumber = 4;
    private static readonly char IndentSymbol = ' ';
    private static readonly string SingleIndent = new string(IndentSymbol, 4);
    private string CurrentIndent => string.Concat(Enumerable.Repeat(SingleIndent, _indent));


    public void Block(Func<string> blockGeneration)
    {
        Block(writer => writer.Write(blockGeneration()));
    }

    public void NewLine() => Write("\r\n");

    public string GetCode() => _code.ToString();
}