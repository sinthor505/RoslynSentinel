namespace RoslynSentinel.Common;

public struct SymbolHandle
{
    public string SymbolName
    {
        get; init;
    }
    public string ProjectName
    {
        get; init;
    }
    private string _docCommentId;
    public string DocCommentId
    {
        get
        {
            return _docCommentId;
        }
        init
        {
            _docCommentId = value;
        }
    }
    public string SymbolId
    {
        get
        {
            return _docCommentId;
        }
        init
        {
            _docCommentId = value;
        }
    }

    public SymbolHandle(string symbolName, string symbolId, string projectName)
    {
        SymbolName = symbolName;
        _docCommentId = symbolId;
        ProjectName = projectName;
    }
}
