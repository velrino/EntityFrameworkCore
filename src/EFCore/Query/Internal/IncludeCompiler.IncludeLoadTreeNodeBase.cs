﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Metadata;
using Remotion.Linq;
using Remotion.Linq.Clauses.Expressions;
using Remotion.Linq.Clauses.ResultOperators;
using Remotion.Linq.Parsing;
using Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    public partial class IncludeCompiler
    {
        private abstract class IncludeLoadTreeNodeBase
        {
            protected static void AddLoadPath(
                IncludeLoadTreeNodeBase node,
                IReadOnlyList<INavigation> navigationPath,
                int index)
            {
                while (index < navigationPath.Count)
                {
                    var navigation = navigationPath[index];
                    var childNode = node.Children.SingleOrDefault(n => n.Navigation == navigation);

                    if (childNode == null)
                    {
                        node.Children.Add(childNode = new IncludeLoadTreeNode(navigation));
                    }

                    node = childNode;
                    index = index + 1;
                }
            }

            protected ICollection<IncludeLoadTreeNode> Children { get; } = new List<IncludeLoadTreeNode>();

            protected void Compile(
                QueryCompilationContext queryCompilationContext,
                QueryModel queryModel,
                bool trackingQuery,
                bool asyncQuery,
                ref int collectionIncludeId,
                QuerySourceReferenceExpression targetQuerySourceReferenceExpression)
            {
                var entityParameter
                    = Expression.Parameter(targetQuerySourceReferenceExpression.Type, name: "entity");

                var propertyExpressions = new List<Expression>();
                var blockExpressions = new List<Expression>();

                if (trackingQuery)
                {
                    blockExpressions.Add(
                        Expression.Call(
                            Expression.Property(
                                EntityQueryModelVisitor.QueryContextParameter,
                                nameof(QueryContext.QueryBuffer)),
                            _queryBufferStartTrackingMethodInfo,
                            entityParameter,
                            Expression.Constant(
                                queryCompilationContext.FindEntityType(targetQuerySourceReferenceExpression.ReferencedQuerySource)
                                ?? queryCompilationContext.Model.FindEntityType(entityParameter.Type))));
                }

                var includedIndex = 0;

                // ReSharper disable once LoopCanBeConvertedToQuery
                foreach (var includeLoadTreeNode in Children)
                {
                    blockExpressions.Add(
                        includeLoadTreeNode.Compile(
                            queryCompilationContext,
                            targetQuerySourceReferenceExpression,
                            entityParameter,
                            propertyExpressions,
                            trackingQuery,
                            asyncQuery,
                            ref includedIndex,
                            ref collectionIncludeId));
                }

                if (blockExpressions.Count > 1
                    || blockExpressions.Count == 1
                    && !trackingQuery)
                { 
                    AwaitTaskExpressions(asyncQuery, blockExpressions);

                    var includeExpression
                        = blockExpressions.Last().Type == typeof(Task)
                            ? (Expression)Expression.Property(
                                Expression.Call(
                                    _includeAsyncMethodInfo
                                        .MakeGenericMethod(targetQuerySourceReferenceExpression.Type),
                                    EntityQueryModelVisitor.QueryContextParameter,
                                    targetQuerySourceReferenceExpression,
                                    Expression.NewArrayInit(typeof(object), propertyExpressions),
                                    Expression.Lambda(
                                        Expression.Block(blockExpressions),
                                        EntityQueryModelVisitor.QueryContextParameter,
                                        entityParameter,
                                        _includedParameter,
                                        _cancellationTokenParameter),
                                    _cancellationTokenParameter),
                                nameof(Task<object>.Result))
                            : Expression.Call(
                                _includeMethodInfo.MakeGenericMethod(targetQuerySourceReferenceExpression.Type),
                                EntityQueryModelVisitor.QueryContextParameter,
                                targetQuerySourceReferenceExpression,
                                Expression.NewArrayInit(typeof(object), propertyExpressions),
                                Expression.Lambda(
                                    Expression.Block(typeof(void), blockExpressions),
                                    EntityQueryModelVisitor.QueryContextParameter,
                                    entityParameter,
                                    _includedParameter));

                    var includeApplyingVisitor = new IncludeApplyingQueryModelVisitor(
                        targetQuerySourceReferenceExpression,
                        includeExpression);


                    //// MLG haxx - first apply to top level, and only on second pass go deep
                    //ApplyIncludeExpressionsToQueryModel(
                    //    queryModel, targetQuerySourceReferenceExpression, includeExpression);

                    includeApplyingVisitor.VisitQueryModel(queryModel);
                }
            }

            private class IncludeApplyingQueryModelVisitor : QueryModelVisitorBase
            {
                public IncludeApplyingQueryModelVisitor(
                    QuerySourceReferenceExpression querySourceReferenceExpression,
                    Expression expression)
                {
                    _querySourceReferenceExpression = querySourceReferenceExpression;
                    _expression = expression;
                }

                private readonly QuerySourceReferenceExpression _querySourceReferenceExpression;
                private readonly Expression _expression;

                public override void VisitQueryModel(QueryModel queryModel)
                {
                    queryModel.TransformExpressions(new TransformingQueryModelExpressionVisitor<IncludeApplyingQueryModelVisitor>(this).Visit);

                    ApplyIncludeExpressionsToQueryModel(queryModel, _querySourceReferenceExpression, _expression);

                    base.VisitQueryModel(queryModel);
                }
            }

            protected static void ApplyIncludeExpressionsToQueryModel(
                QueryModel queryModel,
                QuerySourceReferenceExpression querySourceReferenceExpression,
                Expression expression)
            {
                var includeReplacingExpressionVisitor = new IncludeReplacingExpressionVisitor();

                foreach (var groupResultOperator
                    in queryModel.ResultOperators.OfType<GroupResultOperator>())
                {
                    var newElementSelector
                        = includeReplacingExpressionVisitor.Replace(
                            querySourceReferenceExpression,
                            expression,
                            groupResultOperator.ElementSelector);

                    if (!ReferenceEquals(newElementSelector, groupResultOperator.ElementSelector))
                    {
                        groupResultOperator.ElementSelector = newElementSelector;

                        return;
                    }
                }

                queryModel.SelectClause.TransformExpressions(
                    e => includeReplacingExpressionVisitor.Replace(
                        querySourceReferenceExpression,
                        expression,
                        e));
            }

            protected static void AwaitTaskExpressions(bool asyncQuery, List<Expression> blockExpressions)
            {
                if (asyncQuery)
                {
                    var taskExpressions = new List<Expression>();

                    foreach (var expression in blockExpressions.ToArray())
                    {
                        if (expression.Type == typeof(Task))
                        {
                            blockExpressions.Remove(expression);
                            taskExpressions.Add(expression);
                        }
                    }

                    if (taskExpressions.Count > 0)
                    {
                        blockExpressions.Add(
                            taskExpressions.Count == 1
                                ? taskExpressions[0]
                                : Expression.Call(
                                    _awaitManyMethodInfo,
                                    Expression.NewArrayInit(
                                        typeof(Func<Task>),
                                        taskExpressions.Select(e => Expression.Lambda(e)))));
                    }
                }
            }

            private static readonly MethodInfo _awaitManyMethodInfo
                = typeof(IncludeLoadTreeNodeBase).GetTypeInfo()
                    .GetDeclaredMethod(nameof(_AwaitMany));

            // ReSharper disable once InconsistentNaming
            private static async Task _AwaitMany(IReadOnlyList<Func<Task>> taskFactories)
            {
                // ReSharper disable once ForCanBeConvertedToForeach
                for (var i = 0; i < taskFactories.Count; i++)
                {
                    await taskFactories[i]();
                }
            }

            private class IncludeReplacingExpressionVisitor : RelinqExpressionVisitor
            {
                private QuerySourceReferenceExpression _querySourceReferenceExpression;
                private Expression _includeExpression;

                public Expression Replace(
                    QuerySourceReferenceExpression querySourceReferenceExpression,
                    Expression includeExpression,
                    Expression searchedExpression)
                {
                    _querySourceReferenceExpression = querySourceReferenceExpression;
                    _includeExpression = includeExpression;

                    return Visit(searchedExpression);
                }

                protected override Expression VisitQuerySourceReference(
                    QuerySourceReferenceExpression querySourceReferenceExpression)
                {
                    if (ReferenceEquals(querySourceReferenceExpression, _querySourceReferenceExpression))
                    {
                        _querySourceReferenceExpression = null;

                        return _includeExpression;
                    }

                    return querySourceReferenceExpression;
                }
            }
        }
    }
}
