using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RefactorTool
{
    class Program
    {
        static void Main(string[] args)
        {
            var directory = @"C:\Users\apearson\source\personal\octocon\csharp\Interfold.Domain";
            var files = Directory.GetFiles(directory, "*CommandHandler.cs", SearchOption.AllDirectories);
            
            foreach (var file in files)
            {
                if (file.Contains("IdempotentCommandHandler.cs")) continue;

                var code = File.ReadAllText(file);
                if (!code.Contains("ICommandHandler<")) continue;
                if (!code.Contains("_idempotencyStore.FindAsync")) continue; // Only refactor those with idempotency logic

                Console.WriteLine($"Refactoring: {file}");
                
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetRoot();

                var rewriter = new IdempotencyRewriter();
                var newRoot = rewriter.Visit(root);
                
                if (newRoot != root)
                {
                    File.WriteAllText(file, newRoot.ToFullString());
                }
            }
        }
    }

    class IdempotencyRewriter : CSharpSyntaxRewriter
    {
        private string _entityRef = "EntityRefs.Unknown";
        private string _resultType = "UnknownResult";

        public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var baseList = node.BaseList;
            if (baseList == null) return baseNode();

            var commandHandlerBase = baseList.Types.FirstOrDefault(t => t.Type.ToString().StartsWith("ICommandHandler<"));
            if (commandHandlerBase == null) return baseNode();

            var genericArgs = ((GenericNameSyntax)commandHandlerBase.Type).TypeArgumentList.Arguments;
            _resultType = genericArgs[1].ToString();

            // Replace ICommandHandler with IdempotentCommandHandler
            var newCommandBase = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName($"IdempotentCommandHandler<{genericArgs[0]}, {genericArgs[1]}>"))
                .WithTriviaFrom(commandHandlerBase);
            
            var newBaseList = baseList.ReplaceNode(commandHandlerBase, newCommandBase);
            node = node.WithBaseList(newBaseList);

            var newNode = (ClassDeclarationSyntax)base.VisitClassDeclaration(node);

            // Add the missing overrides after the constructor
            if (_entityRef != null)
            {
                var overrideStr = $"\n\n    protected override EntityRef DuplicateEntityRef => {_entityRef};\n\n    protected override {_resultType} CreateReplayResult({_resultType} originalResult) =>\n        originalResult with {{ Replay = true }};\n";
                var parsedMember = SyntaxFactory.ParseMemberDeclaration(overrideStr);
                
                // insert after constructor
                var ctor = newNode.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
                if (ctor != null)
                {
                    var ctorIndex = newNode.Members.IndexOf(ctor);
                    newNode = newNode.WithMembers(newNode.Members.Insert(ctorIndex + 1, parsedMember!));
                }
            }

            return newNode;

            SyntaxNode baseNode() => base.VisitClassDeclaration(node);
        }

        public override SyntaxNode VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            if (node.Declaration.Type.ToString() == "IIdempotencyStore")
            {
                return null; // Remove the field
            }
            return base.VisitFieldDeclaration(node);
        }

        public override SyntaxNode VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            var parameters = node.ParameterList.Parameters;
            var idempotencyParam = parameters.FirstOrDefault(p => p.Type.ToString() == "IIdempotencyStore");
            if (idempotencyParam == null) return base.VisitConstructorDeclaration(node);

            // Add : base(idempotencyStore)
            var initializer = SyntaxFactory.ConstructorInitializer(
                SyntaxKind.BaseConstructorInitializer,
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(idempotencyParam.Identifier.Text))))
            );

            node = node.WithInitializer(initializer);

            // Remove assignment from body
            if (node.Body != null)
            {
                var assignments = node.Body.Statements.Where(s => s.ToString().Contains("_idempotencyStore =")).ToList();
                node = node.WithBody(node.Body.RemoveNodes(assignments, SyntaxRemoveOptions.KeepNoTrivia)!);
            }

            return base.VisitConstructorDeclaration(node);
        }

        public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (node.Identifier.Text == "HandleAsync")
            {
                var modifiers = SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxFactory.TriviaList(), SyntaxKind.ProtectedKeyword, SyntaxFactory.TriviaList(SyntaxFactory.Space)),
                    SyntaxFactory.Token(SyntaxFactory.TriviaList(), SyntaxKind.OverrideKeyword, SyntaxFactory.TriviaList(SyntaxFactory.Space)),
                    SyntaxFactory.Token(SyntaxFactory.TriviaList(), SyntaxKind.AsyncKeyword, SyntaxFactory.TriviaList(SyntaxFactory.Space))
                );

                // Rename to ExecuteCoreAsync and add protected override
                node = node.WithIdentifier(SyntaxFactory.Identifier("ExecuteCoreAsync "))
                    .WithModifiers(modifiers);

                if (node.Body != null)
                {
                    // Find RejectDuplicate call to extract EntityRef
                    var rejectNode = node.Body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                        .FirstOrDefault(i => i.Expression.ToString() == "RejectDuplicate");
                    if (rejectNode != null && rejectNode.ArgumentList.Arguments.Count == 2)
                    {
                        _entityRef = rejectNode.ArgumentList.Arguments[1].ToString();
                    }

                    // Remove all statements up to and including the idempotency check
                    var statements = node.Body.Statements.ToList();
                    
                    var ifPrevious = statements.FirstOrDefault(s => s.ToString().StartsWith("if (previous is not null)"));
                    if (ifPrevious != null)
                    {
                        var index = statements.IndexOf(ifPrevious);
                        // Also remove previous statements related to payload parsing and previous find
                        var toRemove = statements.Take(index + 1).Where(s => s.ToString().Contains("payloadJson") || s.ToString().Contains("payloadHash") || s.ToString().Contains("previous")).ToList();
                        node = node.WithBody(node.Body.RemoveNodes(toRemove, SyntaxRemoveOptions.KeepNoTrivia)!);
                    }
                    
                    // Re-evaluate body statements after first pass
                    statements = node.Body.Statements.ToList();

                    // Remove the save logic
                    var saveStore = statements.FirstOrDefault(s => s.ToString().Contains("_idempotencyStore.SaveAsync"));
                    if (saveStore != null)
                    {
                        var index = statements.IndexOf(saveStore);
                        var toRemoveSave = statements.Skip(index - 1).Take(2).Where(s => s.ToString().Contains("resultJson") || s.ToString().Contains("SaveAsync")).ToList();
                        node = node.WithBody(node.Body.RemoveNodes(toRemoveSave, SyntaxRemoveOptions.KeepNoTrivia)!);
                    }
                }
                return node; // No need to visit deeper
            }
            
            if (node.Identifier.Text == "RejectDuplicate")
            {
                return null; // Remove method
            }

            return base.VisitMethodDeclaration(node);
        }
    }
}
