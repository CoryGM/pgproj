using System.Globalization;
using System.Text.Json;

namespace PgProj.Parser;

public sealed partial class PgQueryParser
{
    /// <summary>
    /// A <c>CREATE CAST</c> statement (G-131): the two types as <c>sourcetype</c>/<c>targettype</c>, the converting function (if any) as
    /// <c>func</c> (<c>objname</c> and the <c>objargs</c> types), <c>context</c> (<c>COERCION_EXPLICIT</c>/<c>ASSIGNMENT</c>/<c>IMPLICIT</c>), and
    /// <c>inout</c> for <c>WITH INOUT</c>. A type is rendered from its tree, without modifiers: PostgreSQL does not keep them for a cast.
    /// </summary>
    private static CastNode? TryCreateCastNode(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "sourcetype", out var source) || !TryGetDirectProperty(element, "targettype", out var target))
        {
            return null;
        }

        var context = TryGetDirectStringProperty(element, "context") switch
        {
            "COERCION_IMPLICIT" => "implicit",
            "COERCION_ASSIGNMENT" => "assignment",
            _ => "explicit",
        };

        string? functionName = null;
        var functionArguments = new List<string>();
        if (TryGetDirectProperty(element, "func", out var function))
        {
            functionName = TryGetQualifiedName(function, "objname");
            if (TryGetDirectProperty(function, "objargs", out var arguments) && arguments.ValueKind == JsonValueKind.Array)
            {
                foreach (var argument in arguments.EnumerateArray())
                {
                    if (TryGetDirectProperty(argument, "TypeName", out var typeName))
                    {
                        functionArguments.Add(RenderCastTypeName(typeName));
                    }
                }
            }
        }

        return new CastNode(RenderCastTypeName(source), RenderCastTypeName(target))
        {
            WrittenTypes = CollectWrittenTypes(element),
            FunctionName = functionName,
            FunctionArguments = functionArguments,
            Context = context,
            IsInOut = TryGetDirectBooleanProperty(element, "inout", out var inOut) && inOut,
        };
    }

    /// <summary>
    /// A <c>CREATE OPERATOR</c> statement (G-129), a <c>DefineStmt</c> of kind <c>OBJECT_OPERATOR</c>: <c>defnames</c> is the operator's name (a schema
    /// part first when one is written) and <c>definition</c> a list of <c>DefElem</c>s: the operand types (<c>leftarg</c>, <c>rightarg</c>, each a
    /// <c>TypeName</c>), the function (<c>function</c> or <c>procedure</c>, a <c>TypeName</c> that holds a name), <c>commutator</c> and <c>negator</c> (a
    /// <c>List</c> of name parts), <c>restrict</c> and <c>join</c> (function names), and the flags <c>hashes</c> and <c>merges</c> (no <c>arg</c>).
    /// </summary>
    private static OperatorNode? TryCreateOperatorNode(JsonElement element)
    {
        var nameParts = TryGetStringListFromDirectProperty(element, "defnames");
        if (nameParts.Count == 0)
        {
            return null;
        }

        var node = new OperatorNode(nameParts[^1], nameParts.Count > 1 ? string.Join('.', nameParts.Take(nameParts.Count - 1)) : null);
        if (!TryGetDirectProperty(element, "definition", out var definition) || definition.ValueKind != JsonValueKind.Array)
        {
            return node;
        }

        foreach (var item in definition.EnumerateArray())
        {
            if (!TryGetDirectProperty(item, "DefElem", out var option))
            {
                continue;
            }

            var optionName = TryGetDirectStringProperty(option, "defname")?.ToLowerInvariant();
            var hasArgument = TryGetDirectProperty(option, "arg", out var argument);
            node = optionName switch
            {
                "leftarg" when hasArgument => node with { LeftType = ReadOperatorTypeName(argument) },
                "rightarg" when hasArgument => node with { RightType = ReadOperatorTypeName(argument) },
                "function" or "procedure" when hasArgument => node with { FunctionName = ReadOperatorName(argument) },
                "commutator" when hasArgument => node with { Commutator = ReadOperatorName(argument) },
                "negator" when hasArgument => node with { Negator = ReadOperatorName(argument) },
                "restrict" when hasArgument => node with { RestrictFunction = ReadOperatorName(argument) },
                "join" when hasArgument => node with { JoinFunction = ReadOperatorName(argument) },
                "hashes" => node with { IsHashes = true },
                "merges" => node with { IsMerges = true },
                _ => node,
            };
        }

        return node with { WrittenTypes = CollectWrittenTypes(element) };
    }

    private static string? ReadOperatorTypeName(JsonElement argument)
        => TryGetDirectProperty(argument, "TypeName", out var typeName) ? RenderCastTypeName(typeName) : null;

    /// <summary>A name option's value: a <c>TypeName</c> (a function) or a <c>List</c> (an operator, possibly written <c>OPERATOR(schema.name)</c>), as its parts joined by dots.</summary>
    private static string? ReadOperatorName(JsonElement argument)
    {
        if (TryGetDirectProperty(argument, "TypeName", out var typeName))
        {
            var parts = TryGetStringListFromDirectProperty(typeName, "names");
            return parts.Count == 0 ? null : string.Join('.', parts);
        }

        if (TryGetDirectProperty(argument, "List", out var list))
        {
            var parts = TryGetStringListFromDirectProperty(list, "items");
            return parts.Count == 0 ? null : string.Join('.', parts);
        }

        return null;
    }

    /// <summary>
    /// A <c>CREATE AGGREGATE</c> statement (G-130), a <c>DefineStmt</c> of kind <c>OBJECT_AGGREGATE</c>: <c>defnames</c> is the name (a schema part first when one is written),
    /// <c>args</c> is the argument list (an empty object for <c>(*)</c>, else a <c>List</c> of <c>FunctionParameter</c>s) followed by the number of direct arguments of an
    /// ordered-set aggregate (<c>-1</c> otherwise), and <c>definition</c> is a list of <c>DefElem</c> options. In the old syntax (<c>oldstyle</c>) the one argument is the
    /// <c>basetype</c> option, and <c>ANY</c> stands for <c>(*)</c>.
    /// </summary>
    private static AggregateNode? TryCreateAggregateNode(JsonElement element)
    {
        var nameParts = TryGetStringListFromDirectProperty(element, "defnames");
        if (nameParts.Count == 0)
        {
            return null;
        }

        var arguments = new List<string>();
        var argumentNames = new List<string?>();
        var directCount = -1;
        if (TryGetDirectProperty(element, "args", out var args) && args.ValueKind == JsonValueKind.Array)
        {
            var position = 0;
            foreach (var item in args.EnumerateArray())
            {
                if (position == 0 && TryGetDirectProperty(item, "List", out var list) && TryGetDirectProperty(list, "items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var parameter in items.EnumerateArray())
                    {
                        if (TryGetDirectProperty(parameter, "FunctionParameter", out var functionParameter) && TryGetDirectProperty(functionParameter, "argType", out var argumentType))
                        {
                            var variadic = string.Equals(TryGetDirectStringProperty(functionParameter, "mode"), "FUNC_PARAM_VARIADIC", StringComparison.Ordinal);
                            arguments.Add((variadic ? "VARIADIC " : string.Empty) + RenderCastTypeName(argumentType));
                            argumentNames.Add(TryGetDirectStringProperty(functionParameter, "name") is { Length: > 0 } argumentName ? argumentName : null);
                        }
                    }
                }
                else if (position == 1 && TryGetDirectProperty(item, "Integer", out var integer) && TryGetDirectInt32Property(integer, "ival", out var value))
                {
                    directCount = value;
                }

                position++;
            }
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryGetDirectProperty(element, "definition", out var definition) && definition.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in definition.EnumerateArray())
            {
                if (!TryGetDirectProperty(item, "DefElem", out var option) || TryGetDirectStringProperty(option, "defname")?.ToLowerInvariant() is not { Length: > 0 } optionName)
                {
                    continue;
                }

                var value = TryGetDirectProperty(option, "arg", out var argument) ? ReadDefinitionValue(argument) : "true";
                if (optionName == "basetype")
                {
                    if (!string.Equals(value, "any", StringComparison.OrdinalIgnoreCase))
                    {
                        arguments.Add(value);
                        argumentNames.Add(null);
                    }

                    continue;
                }

                options[optionName] = value;
            }
        }

        return new AggregateNode(nameParts[^1], nameParts.Count > 1 ? string.Join('.', nameParts.Take(nameParts.Count - 1)) : null)
        {
            WrittenTypes = CollectWrittenTypes(element),
            Arguments = arguments,
            ArgumentNames = argumentNames,
            DirectArgumentCount = directCount,
            Options = options,
        };
    }

    /// <summary>
    /// A <c>CREATE COLLATION</c> statement (G-148), a <c>DefineStmt</c> of kind <c>OBJECT_COLLATION</c>: <c>defnames</c> is the name (a schema part first when one is written) and
    /// <c>definition</c> is a list of <c>DefElem</c> options (<c>provider</c> is a type name, the others strings). <c>LOCALE</c> sets both <c>LC_COLLATE</c> and <c>LC_CTYPE</c> of a
    /// libc collation and is the locale of an ICU or builtin one. The <c>FROM</c> form has no options of its own and is not read (<see cref="IsCollationFromForm"/>).
    /// </summary>
    private static CollationNode? TryCreateCollationNode(JsonElement element)
    {
        var nameParts = TryGetStringListFromDirectProperty(element, "defnames");
        if (nameParts.Count == 0 || IsCollationFromForm(element))
        {
            return null;
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryGetDirectProperty(element, "definition", out var definition) && definition.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in definition.EnumerateArray())
            {
                if (TryGetDirectProperty(item, "DefElem", out var option) && TryGetDirectStringProperty(option, "defname")?.ToLowerInvariant() is { Length: > 0 } optionName)
                {
                    options[optionName] = TryGetDirectProperty(option, "arg", out var argument) ? ReadDefinitionValue(argument) : "true";
                }
            }
        }

        var provider = options.TryGetValue("provider", out var providerText) ? providerText.ToLowerInvariant() : "libc";
        options.TryGetValue("locale", out var locale);
        options.TryGetValue("lc_collate", out var lcCollate);
        options.TryGetValue("lc_ctype", out var lcCtype);
        var isLibc = provider == "libc";
        return new CollationNode(nameParts[^1], nameParts.Count > 1 ? string.Join('.', nameParts.Take(nameParts.Count - 1)) : null)
        {
            Provider = provider,
            Locale = isLibc ? null : locale ?? lcCollate,
            LcCollate = isLibc ? lcCollate ?? locale : null,
            LcCtype = isLibc ? lcCtype ?? locale : null,
            IsDeterministic = !(options.TryGetValue("deterministic", out var deterministic) && string.Equals(deterministic, "false", StringComparison.OrdinalIgnoreCase)),
            Rules = options.TryGetValue("rules", out var rules) ? rules : null,
        };
    }

    /// <summary>
    /// A <c>CREATE TEXT SEARCH DICTIONARY</c> statement (G-149), a <c>DefineStmt</c> of kind <c>OBJECT_TSDICTIONARY</c>: <c>defnames</c> is the name and <c>definition</c> the
    /// options as <c>DefElem</c>s; <c>template</c> is a type name (its parts), the others a name, a string, or a number.
    /// </summary>
    private static TextSearchDictionaryNode? TryCreateTextSearchDictionaryNode(JsonElement element)
    {
        var nameParts = TryGetStringListFromDirectProperty(element, "defnames");
        if (nameParts.Count == 0)
        {
            return null;
        }

        string? template = null;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryGetDirectProperty(element, "definition", out var definition) && definition.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in definition.EnumerateArray())
            {
                if (!TryGetDirectProperty(item, "DefElem", out var option) || TryGetDirectStringProperty(option, "defname")?.ToLowerInvariant() is not { Length: > 0 } optionName)
                {
                    continue;
                }

                var value = TryGetDirectProperty(option, "arg", out var argument) ? ReadDefinitionValue(argument) : string.Empty;
                if (optionName == "template")
                {
                    template = value;
                }
                else
                {
                    options[optionName] = value;
                }
            }
        }

        return new TextSearchDictionaryNode(nameParts[^1], nameParts.Count > 1 ? string.Join('.', nameParts.Take(nameParts.Count - 1)) : null)
        {
            Template = template,
            Options = options,
        };
    }

    /// <summary>An <c>ALTER TEXT SEARCH DICTIONARY</c> statement (G-149), an <c>AlterTSDictionaryStmt</c>: <c>dictname</c> and <c>options</c>; an option with no <c>arg</c> is removed.</summary>
    private static TextSearchDictionaryAlterNode? TryCreateTextSearchDictionaryAlterNode(JsonElement element)
    {
        var nameParts = TryGetStringListFromDirectProperty(element, "dictname");
        if (nameParts.Count == 0)
        {
            return null;
        }

        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (TryGetDirectProperty(element, "options", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (TryGetDirectProperty(item, "DefElem", out var option) && TryGetDirectStringProperty(option, "defname")?.ToLowerInvariant() is { Length: > 0 } optionName)
                {
                    options[optionName] = TryGetDirectProperty(option, "arg", out var argument) ? ReadDefinitionValue(argument) : null;
                }
            }
        }

        return new TextSearchDictionaryAlterNode(nameParts[^1], nameParts.Count > 1 ? string.Join('.', nameParts.Take(nameParts.Count - 1)) : null) { Options = options };
    }

    /// <summary>A <c>CREATE TEXT SEARCH CONFIGURATION</c> statement (G-149), a <c>DefineStmt</c> of kind <c>OBJECT_TSCONFIGURATION</c> whose one option is <c>parser</c> or <c>copy</c>.</summary>
    private static TextSearchConfigurationNode? TryCreateTextSearchConfigurationNode(JsonElement element)
    {
        var nameParts = TryGetStringListFromDirectProperty(element, "defnames");
        if (nameParts.Count == 0)
        {
            return null;
        }

        string? parser = null;
        string? copy = null;
        if (TryGetDirectProperty(element, "definition", out var definition) && definition.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in definition.EnumerateArray())
            {
                if (!TryGetDirectProperty(item, "DefElem", out var option) || !TryGetDirectProperty(option, "arg", out var argument))
                {
                    continue;
                }

                switch (TryGetDirectStringProperty(option, "defname")?.ToLowerInvariant())
                {
                    case "parser":
                        parser = ReadDefinitionValue(argument);
                        break;
                    case "copy":
                        copy = ReadDefinitionValue(argument);
                        break;
                }
            }
        }

        return new TextSearchConfigurationNode(nameParts[^1], nameParts.Count > 1 ? string.Join('.', nameParts.Take(nameParts.Count - 1)) : null)
        {
            Parser = parser,
            CopyFrom = copy,
        };
    }

    /// <summary>
    /// An <c>ALTER TEXT SEARCH CONFIGURATION ... MAPPING FOR</c> statement (G-149), an <c>AlterTSConfigurationStmt</c> of kind <c>ALTER_TSCONFIG_ADD_MAPPING</c>,
    /// <c>ALTER_TSCONFIG_ALTER_MAPPING_FOR_TOKEN</c>, or <c>ALTER_TSCONFIG_DROP_MAPPING</c>: <c>cfgname</c>, <c>tokentype</c> (strings), and <c>dicts</c> (a list of name lists).
    /// The <c>REPLACE</c> kinds are not read (<see cref="IsModeledTextSearchMappingKind"/>).
    /// </summary>
    private static TextSearchMappingNode? TryCreateTextSearchMappingNode(JsonElement element)
    {
        var action = TryGetDirectStringProperty(element, "kind") switch
        {
            "ALTER_TSCONFIG_ADD_MAPPING" => TextSearchMappingAction.Add,
            "ALTER_TSCONFIG_ALTER_MAPPING_FOR_TOKEN" => TextSearchMappingAction.Alter,
            "ALTER_TSCONFIG_DROP_MAPPING" => TextSearchMappingAction.Drop,
            _ => (TextSearchMappingAction?)null,
        };
        var nameParts = TryGetStringListFromDirectProperty(element, "cfgname");
        if (action is null || nameParts.Count == 0)
        {
            return null;
        }

        var dictionaries = new List<string>();
        if (TryGetDirectProperty(element, "dicts", out var dicts) && dicts.ValueKind == JsonValueKind.Array)
        {
            dictionaries.AddRange(dicts.EnumerateArray().Select(JoinNameList).OfType<string>());
        }

        return new TextSearchMappingNode(nameParts[^1], nameParts.Count > 1 ? string.Join('.', nameParts.Take(nameParts.Count - 1)) : null)
        {
            Action = action.Value,
            Tokens = [.. TryGetStringListFromDirectProperty(element, "tokentype").Select(static token => token.ToLowerInvariant())],
            Dictionaries = dictionaries,
        };
    }

    /// <summary>True for the <c>ALTER TEXT SEARCH CONFIGURATION</c> kinds that <see cref="TryCreateTextSearchMappingNode"/> reads.</summary>
    internal static bool IsModeledTextSearchMappingKind(string? kind)
        => kind is "ALTER_TSCONFIG_ADD_MAPPING" or "ALTER_TSCONFIG_ALTER_MAPPING_FOR_TOKEN" or "ALTER_TSCONFIG_DROP_MAPPING";

    /// <summary><c>CREATE COLLATION name FROM other</c>: a <c>DefineStmt</c> whose only option is <c>from</c>.</summary>
    internal static bool IsCollationFromForm(JsonElement defineStatement)
        => TryGetDirectProperty(defineStatement, "definition", out var definition)
            && definition.ValueKind == JsonValueKind.Array
            && definition.EnumerateArray().Any(static item => TryGetDirectProperty(item, "DefElem", out var option)
                && string.Equals(TryGetDirectStringProperty(option, "defname"), "from", StringComparison.OrdinalIgnoreCase));

    /// <summary>The value of a <c>DefElem</c> option as text: a type or function name (<c>TypeName</c>), an operator (<c>List</c>), a string, an integer, or a number.</summary>
    private static string ReadDefinitionValue(JsonElement argument)
    {
        if (TryGetDirectProperty(argument, "TypeName", out var typeName))
        {
            return RenderCastTypeName(typeName);
        }

        if (TryGetDirectProperty(argument, "List", out var list))
        {
            return string.Join('.', TryGetStringListFromDirectProperty(list, "items"));
        }

        if (TryGetDirectProperty(argument, "String", out var text))
        {
            return TryGetDirectStringProperty(text, "sval") ?? string.Empty;
        }

        if (TryGetDirectProperty(argument, "Integer", out var integer))
        {
            return TryGetDirectInt32Property(integer, "ival", out var value) ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
        }

        if (TryGetDirectProperty(argument, "Float", out var number))
        {
            return TryGetDirectStringProperty(number, "fval") ?? string.Empty;
        }

        return argument.GetRawText();
    }

    /// <summary>
    /// A <c>CREATE PUBLICATION</c> statement (G-134), a <c>CreatePublicationStmt</c>: <c>pubname</c>, <c>for_all_tables</c>, <c>options</c> (<c>DefElem</c>s whose value is a
    /// string, a boolean, or a number) and <c>pubobjects</c>, each a <c>PublicationObjSpec</c> that is a table (<c>PUBLICATIONOBJ_TABLE</c>, with its relation, column list, and
    /// row filter), a schema (<c>PUBLICATIONOBJ_TABLES_IN_SCHEMA</c>, its <c>name</c>), or the current schema (<c>PUBLICATIONOBJ_TABLES_IN_CUR_SCHEMA</c>).
    /// </summary>
    private static PublicationNode? TryCreatePublicationNode(JsonElement element)
    {
        if (TryGetDirectStringProperty(element, "pubname") is not { Length: > 0 } name)
        {
            return null;
        }

        ReadPublicationObjects(element, out var tables, out var schemas);
        var options = ReadPublicationOptions(element);

        return new PublicationNode(name)
        {
            IsAllTables = TryGetDirectBooleanProperty(element, "for_all_tables", out var allTables) && allTables,
            Tables = tables,
            Schemas = schemas,
            Options = options,
        };
    }

    /// <summary>
    /// An <c>ALTER PUBLICATION</c> statement (G-258), an <c>AlterPublicationStmt</c>: <c>pubname</c>, the same <c>pubobjects</c> and <c>options</c> as <c>CREATE PUBLICATION</c>, and an
    /// <c>action</c> (<c>AP_AddObjects</c>, <c>AP_DropObjects</c>, <c>AP_SetObjects</c>); <c>SET (...)</c> has options and the add action.
    /// </summary>
    private static AlterPublicationNode? TryCreateAlterPublicationNode(JsonElement element)
    {
        if (TryGetDirectStringProperty(element, "pubname") is not { Length: > 0 } name)
        {
            return null;
        }

        ReadPublicationObjects(element, out var tables, out var schemas);
        return new AlterPublicationNode(
            name,
            TryGetDirectStringProperty(element, "action") switch { "AP_DropObjects" => "DROP", "AP_SetObjects" => "SET", _ => "ADD" })
        {
            Tables = tables,
            Schemas = schemas,
            Options = ReadPublicationOptions(element),
        };
    }

    private static void ReadPublicationObjects(JsonElement element, out List<PublicationTableNode> tables, out List<string> schemas)
    {
        tables = [];
        schemas = [];
        if (TryGetDirectProperty(element, "pubobjects", out var objects) && objects.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in objects.EnumerateArray())
            {
                if (!TryGetDirectProperty(item, "PublicationObjSpec", out var spec))
                {
                    continue;
                }

                switch (TryGetDirectStringProperty(spec, "pubobjtype"))
                {
                    case "PUBLICATIONOBJ_TABLE" when TryGetDirectProperty(spec, "pubtable", out var publicationTable)
                        && TryGetRelationIdentity(publicationTable, "relation", out var tableName, out var schemaName):
                        var hasFilter = TryGetDirectProperty(publicationTable, "whereClause", out var filter);
                        tables.Add(new PublicationTableNode(schemaName, tableName)
                        {
                            Columns = TryGetStringListFromDirectProperty(publicationTable, "columns"),
                            RawFilter = hasFilter ? ReadExpressionText(filter) : null,
                            FilterAst = hasFilter ? filter.GetRawText() : null,
                        });
                        break;
                    case "PUBLICATIONOBJ_TABLES_IN_SCHEMA" when TryGetDirectStringProperty(spec, "name") is { Length: > 0 } schema:
                        schemas.Add(schema);
                        break;
                    case "PUBLICATIONOBJ_TABLES_IN_CUR_SCHEMA":
                        schemas.Add("CURRENT_SCHEMA");
                        break;
                }
            }
        }

    }

    private static Dictionary<string, string> ReadPublicationOptions(JsonElement element)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryGetDirectProperty(element, "options", out var definition) && definition.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in definition.EnumerateArray())
            {
                if (TryGetDirectProperty(item, "DefElem", out var option) && TryGetDirectStringProperty(option, "defname")?.ToLowerInvariant() is { Length: > 0 } optionName)
                {
                    options[optionName] = TryGetDirectProperty(option, "arg", out var argument) ? ReadDefinitionValue(argument) : "true";
                }
            }
        }

        return options;
    }

    /// <summary>
    /// A <c>CREATE STATISTICS</c> statement (G-150), a <c>CreateStatsStmt</c>: <c>defnames</c> is the name (a schema part first when one is written), <c>stat_types</c> the
    /// kinds as strings, <c>exprs</c> the columns and expressions as <c>StatsElem</c>s (a <c>name</c>, or an <c>expr</c> tree), and <c>relations</c> the one table as a
    /// <c>RangeVar</c>.
    /// </summary>
    private static StatisticsNode? TryCreateStatisticsNode(JsonElement element)
    {
        var nameParts = TryGetStringListFromDirectProperty(element, "defnames");
        if (nameParts.Count == 0)
        {
            return null;
        }

        string? tableName = null;
        string? tableSchema = null;
        if (TryGetDirectProperty(element, "relations", out var relations) && relations.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in relations.EnumerateArray())
            {
                if (TryGetDirectProperty(item, "RangeVar", out var rangeVar))
                {
                    tableName = TryGetDirectStringProperty(rangeVar, "relname");
                    tableSchema = TryGetDirectStringProperty(rangeVar, "schemaname");
                    break;
                }
            }
        }

        var elements = new List<StatisticsElementNode>();
        if (TryGetDirectProperty(element, "exprs", out var exprs) && exprs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in exprs.EnumerateArray())
            {
                if (!TryGetDirectProperty(item, "StatsElem", out var statsElement))
                {
                    continue;
                }

                if (TryGetDirectProperty(statsElement, "expr", out var expression))
                {
                    elements.Add(new StatisticsElementNode(null) { RawExpression = ReadExpressionText(expression), ExpressionAst = expression.GetRawText() });
                }
                else
                {
                    elements.Add(new StatisticsElementNode(TryGetDirectStringProperty(statsElement, "name")));
                }
            }
        }

        return new StatisticsNode(nameParts[^1], nameParts.Count > 1 ? string.Join('.', nameParts.Take(nameParts.Count - 1)) : null)
        {
            TableName = tableName,
            TableSchemaName = tableSchema,
            Kinds = [.. TryGetStringListFromDirectProperty(element, "stat_types").Select(static kind => kind.ToLowerInvariant())],
            Elements = elements,
        };
    }

    /// <summary>A pg_query <c>TypeName</c> as text: its name parts joined by dots (<c>pg_catalog.int4</c>, <c>public.mytype</c>), and <c>[]</c> for each array bound.</summary>
    private static string RenderCastTypeName(JsonElement typeName)
    {
        var name = string.Join('.', TryGetStringListFromDirectProperty(typeName, "names"));
        var bounds = TryGetDirectProperty(typeName, "arrayBounds", out var arrayBounds) && arrayBounds.ValueKind == JsonValueKind.Array
            ? arrayBounds.GetArrayLength()
            : 0;
        return name + string.Concat(Enumerable.Repeat("[]", bounds));
    }

    /// <summary>
    /// A <c>CREATE EVENT TRIGGER</c> statement (G-127): <c>trigname</c>, <c>eventname</c>, the function as <c>funcname</c> (a list of name
    /// parts), and <c>WHEN TAG IN (...)</c> as a <c>whenclause</c> of <c>DefElem</c>s, the tags being the strings of the one named <c>tag</c>.
    /// </summary>
    private static EventTriggerNode? TryCreateEventTriggerNode(JsonElement element)
    {
        var name = TryGetDirectStringProperty(element, "trigname");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var tags = new List<string>();
        if (TryGetDirectProperty(element, "whenclause", out var whenClause) && whenClause.ValueKind == JsonValueKind.Array)
        {
            foreach (var condition in whenClause.EnumerateArray())
            {
                if (TryGetDirectProperty(condition, "DefElem", out var defElem)
                    && string.Equals(TryGetDirectStringProperty(defElem, "defname"), "tag", StringComparison.OrdinalIgnoreCase)
                    && TryGetDirectProperty(defElem, "arg", out var arg)
                    && TryGetDirectProperty(arg, "List", out var list))
                {
                    tags.AddRange(TryGetStringListFromDirectProperty(list, "items"));
                }
            }
        }

        return new EventTriggerNode(name)
        {
            EventName = TryGetDirectStringProperty(element, "eventname"),
            Tags = tags,
            FunctionName = TryGetQualifiedName(element, "funcname"),
        };
    }

    private static PolicyNode? TryCreatePolicyNode(JsonElement element, string statementSql)
    {
        if (!TryGetRelationIdentity(element, "table", out var tableName, out var schemaName))
        {
            return null;
        }

        var policyName = TryGetStringPropertyRecursive(element, "policy_name");
        if (string.IsNullOrWhiteSpace(policyName))
        {
            return null;
        }

        // "permissive" is present as true for an explicit or omitted PERMISSIVE, and absent entirely (not false) for
        // RESTRICTIVE, so its absence must map to false here, not to the PERMISSIVE default.
        var isPermissive = TryGetDirectBooleanProperty(element, "permissive", out var permissiveValue) && permissiveValue;
        var command = TryGetDirectStringProperty(element, "cmd_name") is { Length: > 0 } cmdName ? cmdName.ToLowerInvariant() : "all";

        string? rawUsingExpression = null;
        string? usingExpressionAst = null;
        if (TryGetDirectProperty(element, "qual", out var qualElement))
        {
            usingExpressionAst = qualElement.GetRawText();
            rawUsingExpression = ConstraintIndexTextReader.TryReadPolicyUsingExpression(statementSql) ?? ReadExpressionText(qualElement);
        }

        string? rawWithCheckExpression = null;
        string? withCheckExpressionAst = null;
        if (TryGetDirectProperty(element, "with_check", out var withCheckElement))
        {
            withCheckExpressionAst = withCheckElement.GetRawText();
            rawWithCheckExpression = ConstraintIndexTextReader.TryReadPolicyWithCheckExpression(statementSql) ?? ReadExpressionText(withCheckElement);
        }

        return new PolicyNode(policyName, schemaName, tableName)
        {
            Command = command,
            IsPermissive = isPermissive,
            Roles = ReadPolicyRoleNames(element),
            RawUsingExpression = rawUsingExpression,
            UsingExpressionAst = usingExpressionAst,
            RawWithCheckExpression = rawWithCheckExpression,
            WithCheckExpressionAst = withCheckExpressionAst,
        };
    }

    /// <summary>
    /// The roles of a <c>CREATE POLICY</c>'s <c>TO</c> clause (G-76), defaulting to <c>["PUBLIC"]</c> when the array is absent
    /// or empty. Mirrors <c>GrantStatementReader.ReadRoleName</c>'s <c>RoleSpec</c> mapping; not shared with it, since that
    /// reader is private to a differently-shaped statement and this codebase's convention is a small duplicate per reader
    /// rather than widening an unrelated internal class's surface for one new caller.
    /// </summary>
    private static IReadOnlyList<string> ReadPolicyRoleNames(JsonElement element)
    {
        if (!TryGetDirectProperty(element, "roles", out var rolesElement) || rolesElement.ValueKind != JsonValueKind.Array)
        {
            return ["PUBLIC"];
        }

        var roles = new List<string>();
        foreach (var entry in rolesElement.EnumerateArray())
        {
            if (!TryGetDirectProperty(entry, "RoleSpec", out var role))
            {
                continue;
            }

            var name = TryGetDirectStringProperty(role, "roletype") switch
            {
                "ROLESPEC_PUBLIC" => "PUBLIC",
                "ROLESPEC_CSTRING" => TryGetDirectStringProperty(role, "rolename"),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(name))
            {
                roles.Add(name);
            }
        }

        return roles.Count == 0 ? ["PUBLIC"] : roles;
    }
}
