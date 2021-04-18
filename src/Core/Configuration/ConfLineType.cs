namespace SS.Core.Configuration
{
    /// <summary>
    /// The type of a line in a <see cref="ConfFile"/>.
    /// </summary>
    public enum ConfLineType
    {
        /// <summary>
        /// An empty line.
        /// </summary>
        Empty,

        /// <summary>
        /// A comment.
        /// </summary>
        /// <example><code>; This is a comment</code></example>
        /// <example><code>/ This is another comment</code></example>
        Comment,

        /// <summary>
        /// A section.
        /// </summary>
        /// <example><code>[MySection]</code></example>
        Section,

        /// <summary>
        /// A property which can contain a key-value pair or just a key.
        /// </summary>
        /// <example>A key with a value:<code>SomeKey=SomeValue</code></example>
        /// <example>A key without a value:<code>AnotherKey=</code></example>
        /// <example>
        /// A key without a value or delimiter (e.g. how capabilities are specified in groupdef.conf):
        /// <code>higher_than_mod</code>
        /// </example>
        Property,

        /// <summary>
        /// A preprocessor include.
        /// </summary>
        /// <example><code>#include another.conf</code></example>
        /// <example><code>#include "foo.conf"</code></example>
        /// <example><code>#include "someFolder/bar.conf"</code></example>
        PreprocessorInclude,

        /// <summary>
        /// A preprocessor define
        /// </summary>
        /// <example><code>#define Foo</code></example>
        PreprocessorDefine,

        /// <summary>
        /// A preprocessor undefine
        /// </summary>
        /// <example><code>#undef Foo</code></example>
        PreprocessorUndef,

        /// <summary>
        /// A preprocessor ifdef
        /// </summary>
        /// <example><code>#ifdef Foo</code></example>
        PreprocessorIfdef,

        /// <summary>
        /// A preprocessor ifndef
        /// </summary>
        /// <example><code>#ifndef Foo</code></example>
        PreprocessorIfndef,

        /// <summary>
        /// A preprocessor else
        /// </summary>
        /// <example><code>#else</code></example>
        PreprocessorElse,

        /// <summary>
        /// A preprocessor endif
        /// </summary>
        /// <example><code>#endif</code></example>
        PreprocessorEndif,

        /// <summary>
        /// A line that could not be parsed.
        /// </summary>
        ParseError,
    }
}
