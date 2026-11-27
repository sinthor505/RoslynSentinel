namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription;

/// <summary>
/// Slip-first family of 36 one-line members, each with a short XML doc comment holding escaped angle brackets, a generic cref in
/// brace form and a paramref; changes edit words or the cref's type name. Not registered until a later step.
/// </summary>
internal static class DocCommentsSlipScenario
{
    private static readonly SlipSiteSet Sites = new(
        FilePath: "ContosoOrders.Core/Evals/DocCommentsSlip/DocCommentCatalog.cs",
        Namespace: "ContosoOrders.Evals.DocCommentsSlip",
        ClassName: "DocCommentCatalog",
        Preamble: "",
        Sites:
        [
            Changed("KeyOrder", """
                /// <summary>Checks that the left key sorts &lt; the right key.</summary>
                /// <remarks>Backed by <see cref="Dictionary{TKey, TValue}"/> lookups on <paramref name="value"/>.</remarks>
                /// <returns>The key position, unchanged.</returns>
                public static int KeyOrder(int value) => value;
                """, "Dictionary", "SortedDictionary", "unchanged", "as supplied"),
            Changed("NestDepth", """
                /// <summary>Reports the nesting depth when a &gt; b at each level.</summary>
                /// <remarks>Mirrors <see cref="Stack{T}"/> pushes counted in <paramref name="value"/>.</remarks>
                /// <returns>The depth reached.</returns>
                public static int NestDepth(int value) => value;
                """, "Stack", "Queue", "nesting", "call"),
            Changed("BitLimit", """
                /// <summary>Checks that the shift &lt; limit holds.</summary>
                /// <remarks>Uses <see cref="HashSet{T}"/> of <paramref name="value"/> bit flags.</remarks>
                /// <returns>The masked count.</returns>
                public static int BitLimit(int value) => value;
                """, "limit", "bound", "masked", "raw"),
            Control("SetSize", """
                /// <summary>Counts set items when a &lt; b is false.</summary>
                /// <remarks>Reads <see cref="HashSet{T}"/> sizes for <paramref name="value"/>.</remarks>
                /// <returns>The set size.</returns>
                public static int SetSize(int value) => value;
                """),
            Changed("LinkSpan", """
                /// <summary>Measures a span when the gap &gt; 0 between nodes.</summary>
                /// <remarks>Walks <see cref="LinkedList{T}"/> nodes up to <paramref name="value"/>.</remarks>
                /// <returns>The node distance.</returns>
                public static int LinkSpan(int value) => value;
                """, "LinkedList", "List", "distance", "total"),
            Changed("PairWeight", """
                /// <summary>Weighs the pair when left &lt; right by design.</summary>
                /// <remarks>Pairs come from <see cref="KeyValuePair{TKey, TValue}"/> for <paramref name="value"/>.</remarks>
                /// <returns>The pair weight, rounded.</returns>
                public static int PairWeight(int value) => value;
                """, "KeyValuePair", "SortedList", "rounded", "exact"),
            Changed("DeferInit", """
                /// <summary>Defers work whenever a &gt; b is true.</summary>
                /// <remarks>Wraps <see cref="Lazy{T}"/> creation around <paramref name="value"/>.</remarks>
                /// <returns>The deferred result.</returns>
                public static int DeferInit(int value) => value;
                """, "Lazy", "Nullable", "deferred", "cached"),
            Changed("LineWait", """
                /// <summary>Waits in line while a &lt; b remains true.</summary>
                /// <remarks>Drains <see cref="Queue{T}"/> items counted by <paramref name="value"/>.</remarks>
                /// <returns>The wait time.</returns>
                public static int LineWait(int value) => value;
                """, "Queue", "Stack", "time", "ticks"),
            Changed("SortedPick", """
                /// <summary>Picks the smallest item; ties use &gt; first.</summary>
                /// <remarks>Reads <see cref="SortedSet{T}"/> minimums for <paramref name="value"/>.</remarks>
                /// <returns>The chosen item.</returns>
                public static int SortedPick(int value) => value;
                """, "SortedSet", "HashSet", "chosen", "picked"),
            Control("ReadOnlyCount", """
                /// <summary>Counts rows that satisfy a &gt; b.</summary>
                /// <remarks>Wraps <see cref="IReadOnlyCollection{T}"/> counts for <paramref name="value"/>.</remarks>
                /// <returns>The row count.</returns>
                public static int ReadOnlyCount(int value) => value;
                """),
            Changed("ItemCap", """
                /// <summary>Caps the item total when a &lt; b fails.</summary>
                /// <remarks>Sizes <see cref="IReadOnlyList{T}"/> views of <paramref name="value"/> items.</remarks>
                /// <returns>The capped total.</returns>
                public static int ItemCap(int value) => value;
                """, "IReadOnlyList", "IList", "capped", "limited"),
            Changed("MapSlot", """
                /// <summary>Finds a slot where the id &gt; zero.</summary>
                /// <remarks>Indexes <see cref="IDictionary{TKey, TValue}"/> data by <paramref name="value"/>.</remarks>
                /// <returns>The slot index.</returns>
                public static int MapSlot(int value) => value;
                """, "IDictionary", "IReadOnlyDictionary", "slot index", "slot number"),
            Changed("JobCost", """
                /// <summary>Prices a job when cost &lt; budget only.</summary>
                /// <remarks>Awaits <see cref="Task{TResult}"/> results sized by <paramref name="value"/>.</remarks>
                /// <returns>The job cost.</returns>
                public static int JobCost(int value) => value;
                """, "Task", "ValueTask", "job cost", "unit cost"),
            Changed("SliceSum", """
                /// <summary>Adds a span of items while a &gt; b holds.</summary>
                /// <remarks>Slices <see cref="Span{T}"/> data using <paramref name="value"/>.</remarks>
                /// <returns>The slice total.</returns>
                public static int SliceSum(int value) => value;
                """, "Span", "ReadOnlySpan", "total", "sum"),
            Changed("CompareKeys", """
                /// <summary>Orders two keys; negative means left &lt; right.</summary>
                /// <remarks>Delegates to <see cref="Comparer{T}"/> for <paramref name="value"/>.</remarks>
                /// <returns>The sign result.</returns>
                public static int CompareKeys(int value) => value;
                """, "Comparer", "EqualityComparer", "sign", "order"),
            Control("EnumStep", """
                /// <summary>Steps once when index &lt; length.</summary>
                /// <remarks>Advances <see cref="IEnumerator{T}"/> state for <paramref name="value"/>.</remarks>
                /// <returns>The step result.</returns>
                public static int EnumStep(int value) => value;
                """),
            Changed("SetFlag", """
                /// <summary>Flags a member when the set &gt; empty.</summary>
                /// <remarks>Tests <see cref="ISet{T}"/> overlap with <paramref name="value"/>.</remarks>
                /// <returns>The flag count.</returns>
                public static int SetFlag(int value) => value;
                """, "ISet", "IReadOnlySet", "flag count", "flag total"),
            Changed("ItemSeq", """
                /// <summary>Streams items while the cursor &lt; end.</summary>
                /// <remarks>Pulls from <see cref="IEnumerable{T}"/> sources sized by <paramref name="value"/>.</remarks>
                /// <returns>The item count.</returns>
                public static int ItemSeq(int value) => value;
                """, "IEnumerable", "ICollection", "item count", "row count"),
            Changed("EqualPair", """
                /// <summary>Treats two keys as same when a &gt; b is false.</summary>
                /// <remarks>Asks <see cref="IEqualityComparer{T}"/> about <paramref name="value"/>.</remarks>
                /// <returns>The match flag.</returns>
                public static int EqualPair(int value) => value;
                """, "IEqualityComparer", "IComparer", "match", "hit"),
            Changed("SortedRank", """
                /// <summary>Ranks an entry so a smaller key gives a &lt; rank.</summary>
                /// <remarks>Reads <see cref="SortedDictionary{TKey, TValue}"/> order for <paramref name="value"/>.</remarks>
                /// <returns>The rank number.</returns>
                public static int SortedRank(int value) => value;
                """, "SortedDictionary", "Dictionary", "rank number", "place number"),
            Changed("TableLookup", """
                /// <summary>Looks up a row when the key &gt; zero.</summary>
                /// <remarks>Reads <see cref="IReadOnlyDictionary{TKey, TValue}"/> rows for <paramref name="value"/>.</remarks>
                /// <returns>The row id.</returns>
                public static int TableLookup(int value) => value;
                """, "IReadOnlyDictionary", "IDictionary", "row id", "row key"),
            Control("AsyncPull", """
                /// <summary>Pulls one item when ready &gt; zero.</summary>
                /// <remarks>Awaits <see cref="Task{TResult}"/> output for <paramref name="value"/>.</remarks>
                /// <returns>The pulled item.</returns>
                public static int AsyncPull(int value) => value;
                """),
            Changed("ChainRun", """
                /// <summary>Runs steps while a &lt; ceiling holds.</summary>
                /// <remarks>Chains <see cref="Func{T, TResult}"/> steps counted by <paramref name="value"/>.</remarks>
                /// <returns>The step count.</returns>
                public static int ChainRun(int value) => value;
                """, "ceiling", "floor", "step count", "run count"),
            Changed("ValueWrap", """
                /// <summary>Wraps a value when x &gt; y at the boundary.</summary>
                /// <remarks>Boxes <see cref="Nullable{T}"/> data for <paramref name="value"/>.</remarks>
                /// <returns>The wrapped value.</returns>
                public static int ValueWrap(int value) => value;
                """, "Nullable", "Lazy", "wrapped", "boxed"),
            Changed("BufferFill", """
                /// <summary>Fills a buffer; stops when used &lt; size.</summary>
                /// <remarks>Writes <see cref="Memory{T}"/> blocks sized by <paramref name="value"/>.</remarks>
                /// <returns>The filled count.</returns>
                public static int BufferFill(int value) => value;
                """, "Memory", "ReadOnlyMemory", "filled", "written"),
            Changed("PendingWork", """
                /// <summary>Counts pending work where age &gt; allowance.</summary>
                /// <remarks>Tracks <see cref="ValueTask{TResult}"/> handles for <paramref name="value"/>.</remarks>
                /// <returns>The pending count.</returns>
                public static int PendingWork(int value) => value;
                """, "ValueTask", "Task", "pending count", "open count"),
            Changed("EnumFeed", """
                /// <summary>Feeds items from a stream while a &lt; b.</summary>
                /// <remarks>Consumes <see cref="IAsyncEnumerable{T}"/> items for <paramref name="value"/>.</remarks>
                /// <returns>The fed count.</returns>
                public static int EnumFeed(int value) => value;
                """, "IAsyncEnumerable", "IEnumerable", "fed count", "seen count"),
            Control("TupleSize", """
                /// <summary>Sizes a pair when left &lt; right.</summary>
                /// <remarks>Reads <see cref="KeyValuePair{TKey, TValue}"/> parts for <paramref name="value"/>.</remarks>
                /// <returns>The pair size.</returns>
                public static int TupleSize(int value) => value;
                """),
            Changed("HashSlot", """
                /// <summary>Hashes a key into a slot when n &gt; 1.</summary>
                /// <remarks>Uses <see cref="HashSet{T}"/> buckets for <paramref name="value"/>.</remarks>
                /// <returns>The bucket slot.</returns>
                public static int HashSlot(int value) => value;
                """, "HashSet", "SortedSet", "bucket slot", "bucket id"),
            Changed("RangeEnd", """
                /// <summary>Ends a range early if start &lt; end fails.</summary>
                /// <remarks>Maps <see cref="SortedList{TKey, TValue}"/> bounds for <paramref name="value"/>.</remarks>
                /// <returns>The range end.</returns>
                public static int RangeEnd(int value) => value;
                """, "SortedList", "SortedDictionary", "range end", "range tail"),
            Changed("StateMove", """
                /// <summary>Moves a state when depth &gt; zero.</summary>
                /// <remarks>Stores <see cref="List{T}"/> history for <paramref name="value"/>.</remarks>
                /// <returns>The next state.</returns>
                public static int StateMove(int value) => value;
                """, "List", "LinkedList", "next state", "prior state"),
            Changed("CostBand", """
                /// <summary>Bands a cost when price &lt; floor only.</summary>
                /// <remarks>Reads <see cref="IList{T}"/> rows for <paramref name="value"/>.</remarks>
                /// <returns>The cost band.</returns>
                public static int CostBand(int value) => value;
                """, "floor", "cap", "IList", "ICollection"),
            Changed("CountDown", """
                /// <summary>Counts down while a &gt; b keeps holding.</summary>
                /// <remarks>Peeks <see cref="Stack{T}"/> tops for <paramref name="value"/>.</remarks>
                /// <returns>The final count.</returns>
                public static int CountDown(int value) => value;
                """, "Stack", "List", "final", "last"),
            Control("PipeDone", """
                /// <summary>Ends a pipe once a &gt; b is reached.</summary>
                /// <remarks>Drains <see cref="Queue{T}"/> items for <paramref name="value"/>.</remarks>
                /// <returns>The drained count.</returns>
                public static int PipeDone(int value) => value;
                """),
            Changed("MergeKeys", """
                /// <summary>Merges two key sets when a &lt; b first.</summary>
                /// <remarks>Joins <see cref="Dictionary{TKey, TValue}"/> maps for <paramref name="value"/>.</remarks>
                /// <returns>The merged size.</returns>
                public static int MergeKeys(int value) => value;
                """, "Dictionary", "IDictionary", "merged size", "joined size"),
            Changed("TailTrim", """
                /// <summary>Trims a tail when the gap &gt; margin.</summary>
                /// <remarks>Scans <see cref="IReadOnlyList{T}"/> ends for <paramref name="value"/>.</remarks>
                /// <returns>The trimmed length.</returns>
                public static int TailTrim(int value) => value;
                """, "IReadOnlyList", "ICollection", "trimmed", "cut"),
        ]);

    public static ImplementationScenario Definition { get; } = SlipFamilyBuilder.Build(
        "DocCommentsSlip",
        "Edit XML doc comments with escaped angle brackets and generic crefs",
        "plans/plan_slip_first_scenarios.md (DocCommentsSlip)",
        Sites);

    private static SlipSite Changed(string name, string text, string old1, string new1, string old2, string new2) =>
        new(name, text, [new SiteChange(old1, new1), new SiteChange(old2, new2)], SiteRole.Changed);

    private static SlipSite Control(string name, string text) => new(name, text, [], SiteRole.Control);
}
