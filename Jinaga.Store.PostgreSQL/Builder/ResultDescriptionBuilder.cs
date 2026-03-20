using Jinaga.Facts;
using Jinaga.Pipelines;
using Jinaga.Projections;
using Jinaga.Store.PostgreSQL.Description;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Jinaga.Store.PostgreSQL.Builder
{
    internal class ResultDescriptionBuilder
    {
        private ImmutableDictionary<string, int> factTypes;
        private ImmutableDictionary<int, ImmutableDictionary<string, int>> roleMap;

        public ResultDescriptionBuilder(ImmutableDictionary<string, int> factTypes, ImmutableDictionary<int, ImmutableDictionary<string, int>> roleMap)
        {
            this.factTypes = factTypes;
            this.roleMap = roleMap;
        }

        public ResultDescription Build(FactReferenceTuple givenTuple, Specification specification)
        {
            if (givenTuple.Names.Count() != specification.Givens.Count)
            {
                throw new ArgumentException($"The number of start facts ({givenTuple.Names.Count()}) does not match the number of inputs ({specification.Givens.Count}).");
            }
            foreach (var given in specification.Givens)
            {
                var reference = givenTuple.Get(given.Label.Name);
                if (reference.Type != given.Label.Type)
                {
                    throw new ArgumentException($"The start fact type ({reference.Type}) does not match the input type ({given.Label.Type}).");
                }
            }

            var context = ResultDescriptionBuilderContext.Empty;
            return CreateResultDescription(context, specification.Givens, givenTuple, specification.Matches, specification.Projection);
        }

        private ResultDescription CreateResultDescription(ResultDescriptionBuilderContext context, ImmutableList<SpecificationGiven> givens, FactReferenceTuple givenTuple, ImmutableList<Match> matches, Projection projection)
        {
            context = AddEdges(context, givens, givenTuple, matches);

            if (!context.QueryDescription.IsSatisfiable())
            {
                return new ResultDescription(
                    context.QueryDescription,
                    ImmutableDictionary<string, ResultDescription>.Empty
                );
            }

            var childResultDescriptions = ImmutableDictionary<string, ResultDescription>.Empty;
            if (projection is CompoundProjection compoundProjection)
            {
                foreach (var name in compoundProjection.Names)
                {
                    var childProjection = compoundProjection.GetProjection(name);
                    if (childProjection is CollectionProjection collectionProjection)
                    {
                        var resultDescription = CreateResultDescription(
                            context,
                            givens,
                            givenTuple,
                            collectionProjection.Matches,
                            collectionProjection.Projection);
                        childResultDescriptions = childResultDescriptions.Add(name, resultDescription);
                    }
                }
            }

            return new ResultDescription(
                context.QueryDescription,
                childResultDescriptions
            );
        }

        private ResultDescriptionBuilderContext AddEdges(ResultDescriptionBuilderContext context, ImmutableList<SpecificationGiven> givens, FactReferenceTuple givenTuple, ImmutableList<Match> matches)
        {
            foreach (var match in matches)
            {
                var sortedPathConditions = SortPathConditions(context, match.PathConditions);
                foreach (var pathCondition in sortedPathConditions)
                {
                    context = AddPathCondition(context, pathCondition, givens, givenTuple, match.Unknown, "");
                    if (!context.QueryDescription.IsSatisfiable())
                    {
                        return context;
                    }
                }
                foreach (var existentialCondition in match.ExistentialConditions)
                {
                    var contextWithCondition = context.WithExistentialCondition(existentialCondition.Exists);
                    var nestedContext = existentialCondition.Matches.Select(match => match.Unknown)
                        .Aggregate(contextWithCondition, (context, label) => context.WithoutLabel(label));

                    var contextConditional = AddEdges(nestedContext, givens, givenTuple, existentialCondition.Matches);

                    if (contextConditional.QueryDescription.IsSatisfiable())
                    {
                        context = context.WithQueryDescription(contextConditional.QueryDescription);
                    }
                    else if (existentialCondition.Exists)
                    {
                        return ResultDescriptionBuilderContext.Empty;
                    }
                }
            }
            return context;
        }

        private ImmutableList<PathCondition> SortPathConditions(ResultDescriptionBuilderContext context, ImmutableList<PathCondition> pathConditions)
        {
            if (pathConditions.Count <= 1)
            {
                return pathConditions;
            }
            var knownFacts = context.KnownFacts.Keys.ToImmutableHashSet();
            var sortedPathConditions = pathConditions
                .OrderByDescending(pathCondition => knownFacts.Contains(pathCondition.LabelRight))
                .ToImmutableList();

            return sortedPathConditions;
        }

        private ResultDescriptionBuilderContext AddPathCondition(ResultDescriptionBuilderContext context, PathCondition pathCondition, ImmutableList<SpecificationGiven> givens, FactReferenceTuple givenTuple, Label unknown, string v)
        {
            if (!context.KnownFacts.ContainsKey(pathCondition.LabelRight))
            {
                var givenIndex = givens.FindIndex(given => given.Label.Name == pathCondition.LabelRight);
                if (givenIndex < 0)
                {
                    throw new ArgumentException($"No input parameter found for label {pathCondition.LabelRight}");
                }

                var factReference = givenTuple.Get(pathCondition.LabelRight);
                if (!factTypes.ContainsKey(factReference.Type))
                {
                    return context.WithQueryDescription(QueryDescription.Empty);
                }
                int factTypeId = EnsureGetFactTypeId(factReference.Type);
                context = context.WithInputParameter(
                    givens[givenIndex].Label,
                    factTypeId,
                    factReference.Hash
                );
                foreach (var existentialCondition in givens[givenIndex].ExistentialConditions)
                {
                    var contextWithExistentialCondition = context.WithExistentialCondition(existentialCondition.Exists);
                    contextWithExistentialCondition = AddEdges(contextWithExistentialCondition, givens, givenTuple, existentialCondition.Matches);

                    if (contextWithExistentialCondition.QueryDescription.IsSatisfiable())
                    {
                        context = context.WithQueryDescription(contextWithExistentialCondition.QueryDescription);
                    }
                    else if (existentialCondition.Exists)
                    {
                        return context.WithQueryDescription(QueryDescription.Empty);
                    }
                }
            }

            var roleCount = pathCondition.RolesLeft.Count + pathCondition.RolesRight.Count;

            var fact = context.KnownFacts[pathCondition.LabelRight];
            var type = fact.Type;
            var factIndex = fact.FactIndex;
            for (int i = 0; i < pathCondition.RolesRight.Count; i++)
            {
                var role = pathCondition.RolesRight[i];
                if (!factTypes.ContainsKey(type))
                {
                    return context.WithQueryDescription(QueryDescription.Empty);
                }
                var typeId = factTypes[type];

                if (!roleMap.ContainsKey(typeId) || !roleMap[typeId].ContainsKey(role.Name))
                {
                    return context.WithQueryDescription(QueryDescription.Empty);
                }
                var roleId = roleMap[typeId][role.Name];

                if (context.KnownFacts.TryGetValue(unknown.Name, out var knownFact))
                {
                    context = context.WithEdge(knownFact.FactIndex, factIndex, roleId);
                    factIndex = knownFact.FactIndex;
                }
                else
                {
                    var successorFactIndex = factIndex;
                    (context, factIndex) = context.WithFact(role.TargetType);
                    context = context.WithEdge(factIndex, successorFactIndex, roleId);
                }
                
                type = role.TargetType;
            }

            var rightType = type;

            type = unknown.Type;
            var newEdges = ImmutableList<(int roleId, string successorType)>.Empty;
            foreach (var role in pathCondition.RolesLeft)
            {
                if (!factTypes.ContainsKey(type))
                {
                    return context.WithQueryDescription(QueryDescription.Empty);
                }
                var typeId = factTypes[type];

                if (!roleMap.ContainsKey(typeId) || !roleMap[typeId].ContainsKey(role.Name))
                {
                    return context.WithQueryDescription(QueryDescription.Empty);
                }
                var roleId = roleMap[typeId][role.Name];

                newEdges = newEdges.Add((roleId, type));
                type = role.TargetType;
            }

            if (type != rightType)
            {
                throw new ArgumentException($"Type mismatch: {type} is compared to {rightType}");
            }

            for (int i = newEdges.Count - 1; i >= 0; i--)
            {
                var (roleId, successorType) = newEdges[i];
                if (i == 0 && context.KnownFacts.TryGetValue(unknown.Name, out var knownFact))
                {
                    context = context.WithEdge(factIndex, knownFact.FactIndex, roleId);
                    factIndex = knownFact.FactIndex;
                }
                else
                {
                    var predecessorFactIndex = factIndex;
                    (context, factIndex) = context.WithFact(successorType);
                    context = context.WithEdge(predecessorFactIndex, factIndex, roleId);
                }
            }

            context = context.WithLabel(unknown, factIndex);
            return context;
        }

        private int EnsureGetFactTypeId(string type)
        {
            if (!factTypes.ContainsKey(type))
            {
                throw new ArgumentException($"Unknown fact type {type}");
            }
            return factTypes[type];
        }
    }
}
