﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.Data.Entity.Metadata;
using Microsoft.Data.Entity.Query;
using Microsoft.Data.Entity.Relational.Query.Expressions;
using Microsoft.Data.Entity.Relational.Query.ExpressionTreeVisitors;
using Microsoft.Data.Entity.Utilities;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Parsing;

namespace Microsoft.Data.Entity.Relational.Query
{
    public class RelationalQueryModelVisitor : EntityQueryModelVisitor
    {
        private readonly RelationalQueryModelVisitor _parentQueryModelVisitor;

        private readonly Dictionary<IQuerySource, SelectExpression> _queriesBySource
            = new Dictionary<IQuerySource, SelectExpression>();

        private Expression _preOrderingExpression;

        private bool _requiresClientFilter;

        private RelationalProjectionExpressionTreeVisitor _projectionTreeVisitor;

        public RelationalQueryModelVisitor(
            [NotNull] RelationalQueryCompilationContext queryCompilationContext,
            [CanBeNull] RelationalQueryModelVisitor parentQueryModelVisitor)
            : base(Check.NotNull(queryCompilationContext, "queryCompilationContext"))
        {
            _parentQueryModelVisitor = parentQueryModelVisitor;
        }

        public virtual bool RequiresClientFilter => _requiresClientFilter;

        public virtual bool RequiresClientProjection => _projectionTreeVisitor.RequiresClientEval;

        public new virtual RelationalQueryCompilationContext QueryCompilationContext
            => (RelationalQueryCompilationContext)base.QueryCompilationContext;

        public virtual void AddQuery([NotNull] IQuerySource querySource, [NotNull] SelectExpression selectExpression)
        {
            Check.NotNull(querySource, "querySource");
            Check.NotNull(selectExpression, "selectExpression");

            _queriesBySource.Add(querySource, selectExpression);
        }

        public virtual SelectExpression TryGetQuery([NotNull] IQuerySource querySource)
        {
            Check.NotNull(querySource, "querySource");

            SelectExpression selectExpression;
            return (_queriesBySource.TryGetValue(querySource, out selectExpression)
                ? selectExpression
                : _queriesBySource.Values.SingleOrDefault(se => se.HandlesQuerySource(querySource)));
        }

        protected override ExpressionTreeVisitor CreateQueryingExpressionTreeVisitor(IQuerySource querySource)
        {
            Check.NotNull(querySource, "querySource");

            return new RelationalEntityQueryableExpressionTreeVisitor(this, querySource);
        }

        protected override ExpressionTreeVisitor CreateProjectionExpressionTreeVisitor()
        {
            return _projectionTreeVisitor = new RelationalProjectionExpressionTreeVisitor(this);
        }

        protected override ExpressionTreeVisitor CreateOrderingExpressionTreeVisitor(Ordering ordering)
        {
            Check.NotNull(ordering, "ordering");

            return new RelationalOrderingExpressionTreeVisitor(this, ordering);
        }

        protected override void IncludeNavigation(
            IQuerySource querySource,
            Type resultType,
            LambdaExpression accessorLambda,
            IReadOnlyList<INavigation> navigationPath)
        {
            Check.NotNull(querySource, "querySource");
            Check.NotNull(resultType, "resultType");
            Check.NotNull(accessorLambda, "accessorLambda");
            Check.NotNull(navigationPath, "navigationPath");

            var rootEntityType = navigationPath[0].EntityType;

            for (var i = 0; i < navigationPath.Count; i++)
            {
                if (navigationPath[i].IsCollection())
                {
                    IncludeCollection(
                        querySource,
                        rootEntityType,
                        resultType,
                        accessorLambda,
                        navigationPath[i]);
                }
                else
                {
                    var nonCollectionNavigations
                        = navigationPath
                            .Skip(i)
                            .TakeWhile(n => !n.IsCollection()).ToArray();

                    if (nonCollectionNavigations.Any())
                    {
                        IncludeReference(querySource, nonCollectionNavigations);

                        i += nonCollectionNavigations.Length - 1;

                        var propertyExpression
                            = nonCollectionNavigations
                                .Aggregate(accessorLambda.Body,
                                    (e, n) => Expression.Property(e, n.EntityType.Type.GetAnyProperty(n.Name)));

                        accessorLambda
                            = Expression.Lambda(
                                propertyExpression, accessorLambda.Parameters);
                    }
                }
            }
        }

        private void IncludeReference(IQuerySource querySource, IReadOnlyList<INavigation> navigationPath)
        {
            var selectExpression = QueryCompilationContext.FindSelectExpression(querySource);
            var targetTableExpression = selectExpression.FindTableForQuerySource(querySource);

            var readerOffsets = new List<int>();

            foreach (var navigation in navigationPath)
            {
                var targetEntityType = navigation.GetTargetType();
                var targetTableName = QueryCompilationContext.GetTableName(targetEntityType);

                var targetTableAlias
                    = CreateUniqueAlias(selectExpression, targetTableName.First().ToString().ToLower());

                var joinedTableExpression
                    = new TableExpression(
                        targetTableName,
                        QueryCompilationContext.GetSchema(targetEntityType),
                        targetTableAlias,
                        querySource);

                readerOffsets.Add(selectExpression.Projection.Count);

                var columnExpressions
                    = targetEntityType.Properties
                        .Select(p => new ColumnExpression(
                            QueryCompilationContext.GetColumnName(p),
                            p,
                            joinedTableExpression));

                var joinExpression
                    = navigation.ForeignKey.IsRequired
                      && navigation.PointsToPrincipal
                        ? selectExpression
                            .AddInnerJoin(joinedTableExpression, columnExpressions)
                        : selectExpression
                            .AddOuterJoin(joinedTableExpression, columnExpressions);

                joinExpression.Predicate
                    = BuildJoinEqualityExpression(
                        navigation,
                        (navigation.PointsToPrincipal
                            ? targetEntityType
                            : navigation.EntityType)
                            .GetPrimaryKey().Properties,
                        navigation.PointsToPrincipal ? targetTableExpression : joinExpression,
                        navigation.PointsToPrincipal ? joinExpression : targetTableExpression);

                targetTableExpression = joinedTableExpression;
            }

            Expression
                = new IncludeReferenceExpressionTreeVisitor(querySource, navigationPath, readerOffsets)
                    .VisitExpression(Expression);
        }

        private void IncludeCollection(
            IQuerySource querySource,
            IEntityType rootEntityType,
            Type resultType,
            Expression accessorLambda,
            INavigation navigation)
        {
            var selectExpression = QueryCompilationContext.FindSelectExpression(querySource);

            foreach (var property in rootEntityType.GetPrimaryKey().Properties)
            {
                selectExpression
                    .AddToOrderBy(
                        QueryCompilationContext.GetColumnName(property),
                        property,
                        querySource,
                        OrderingDirection.Asc);
            }

            var targetEntityType = navigation.GetTargetType();
            var targetTableName = QueryCompilationContext.GetTableName(targetEntityType);

            var targetSelectExpression = new SelectExpression();

            var targetTableAlias
                = CreateUniqueAlias(selectExpression, targetTableName.First().ToString().ToLower());

            var targetTableExpression
                = new TableExpression(
                    targetTableName,
                    QueryCompilationContext.GetSchema(targetEntityType),
                    targetTableAlias,
                    querySource);

            targetSelectExpression.AddTable(targetTableExpression);

            foreach (var property in targetEntityType.Properties)
            {
                targetSelectExpression
                    .AddToProjection(
                        QueryCompilationContext.GetColumnName(property),
                        property,
                        querySource);
            }

            var innerJoinSelectExpression
                = selectExpression.Clone(
                    ((ColumnExpression)selectExpression.OrderBy.First().Expression).TableAlias);

            innerJoinSelectExpression.IsDistinct = true;
            innerJoinSelectExpression.ClearProjection();
            
            foreach (var columnExpression
                in innerJoinSelectExpression.OrderBy
                    .Select(o => o.Expression)
                    .Cast<ColumnExpression>())
            {
                innerJoinSelectExpression.AddToProjection(columnExpression);
            }

            innerJoinSelectExpression.ClearOrderBy();
            
            var primaryKeyProperties = navigation.EntityType.GetPrimaryKey().Properties;

            if (!innerJoinSelectExpression.IsProjectStar
                && rootEntityType != navigation.EntityType)
            {
                foreach (var primaryKeyProperty in primaryKeyProperties)
                {
                    innerJoinSelectExpression
                        .AddToProjection(
                            new ColumnExpression(
                                QueryCompilationContext.GetColumnName(primaryKeyProperty),
                                primaryKeyProperty,
                                innerJoinSelectExpression.Tables.Last()));
                }
            }

            var innerJoinExpression
                = targetSelectExpression.AddInnerJoin(innerJoinSelectExpression);

            targetSelectExpression.AddToOrderBy(selectExpression.OrderBy);

            innerJoinExpression.Predicate
                = BuildJoinEqualityExpression(
                    navigation,
                    primaryKeyProperties,
                    targetTableExpression,
                    innerJoinExpression);

            var readerParameter = Expression.Parameter(typeof(DbDataReader));

            Expression
                = Expression.Call(
                    QueryCompilationContext.QueryMethodProvider.IncludeCollectionMethod
                        .MakeGenericMethod(resultType),
                    QueryContextParameter,
                    Expression,
                    Expression.Constant(navigation),
                    Expression.Call(
                        QueryCompilationContext.QueryMethodProvider.QueryMethod
                            .MakeGenericMethod(typeof(IValueReader)),
                        QueryContextParameter,
                        Expression.Constant(new CommandBuilder(targetSelectExpression, QueryCompilationContext)),
                        Expression.Lambda(
                            Expression.Call(
                                _createValueReaderForIncludeMethodInfo,
                                QueryContextParameter,
                                readerParameter,
                                Expression.Constant(targetEntityType)),
                            readerParameter)),
                    accessorLambda);
        }

        private static readonly MethodInfo _createValueReaderForIncludeMethodInfo
            = typeof(RelationalQueryModelVisitor).GetTypeInfo()
                .GetDeclaredMethod("CreateValueReaderForInclude");

        [UsedImplicitly]
        private static IValueReader CreateValueReaderForInclude(
            QueryContext queryContext, DbDataReader dataReader, IEntityType entityType)
        {
            return ((RelationalQueryContext)queryContext).ValueReaderFactory.Create(dataReader);
        }

        private static string CreateUniqueAlias(SelectExpression selectExpression, string preferredAlias)
        {
            var alias = preferredAlias;
            var counter = 0;

            while (selectExpression.Projection
                .Any(c => string.Equals(c.TableAlias, alias, StringComparison.OrdinalIgnoreCase)))
            {
                alias = preferredAlias + counter++;
            }

            return alias;
        }

        private Expression BuildJoinEqualityExpression(
            INavigation navigation,
            IReadOnlyList<IProperty> primaryKeyProperties,
            TableExpressionBase targetTableExpression,
            TableExpressionBase joinExpression)
        {
            Expression joinPredicateExpression = null;

            for (var i = 0; i < navigation.ForeignKey.Properties.Count; i++)
            {
                var primaryKeyProperty = primaryKeyProperties[i];
                var foreignKeyProperty = navigation.ForeignKey.Properties[i];

                var foreignKeyColumnExpression
                    = new ColumnExpression(
                        QueryCompilationContext.GetColumnName(foreignKeyProperty),
                        foreignKeyProperty,
                        targetTableExpression);

                var primaryKeyColumnExpression
                    = new ColumnExpression(
                        QueryCompilationContext.GetColumnName(primaryKeyProperty),
                        primaryKeyProperty,
                        joinExpression);
                
                Expression primaryKeyExpression = primaryKeyColumnExpression;

                if (foreignKeyColumnExpression.Type != primaryKeyExpression.Type)
                {
                    if (foreignKeyColumnExpression.Type.IsNullableType()
                        && !primaryKeyExpression.Type.IsNullableType())
                    {
                        primaryKeyExpression
                            = Expression.Convert(primaryKeyExpression, foreignKeyColumnExpression.Type);
                    }
                }

                var equalExpression
                    = Expression.Equal(foreignKeyColumnExpression, primaryKeyExpression);

                joinPredicateExpression
                    = joinPredicateExpression == null
                        ? equalExpression
                        : Expression.AndAlso(joinPredicateExpression, equalExpression);
            }

            return joinPredicateExpression;
        }

        public override void VisitAdditionalFromClause(AdditionalFromClause fromClause, QueryModel queryModel, int index)
        {
            base.VisitAdditionalFromClause(fromClause, queryModel, index);

            var selectExpression = TryGetQuery(fromClause);

            if (selectExpression != null)
            {
                var previousQuerySource
                    = index == 0
                        ? queryModel.MainFromClause
                        : queryModel.BodyClauses[index - 1] as IQuerySource;

                if (previousQuerySource != null)
                {
                    var previousSelectExpression = TryGetQuery(previousQuerySource);

                    if (previousSelectExpression != null)
                    {
                        var readerOffset = previousSelectExpression.Projection.Count;

                        previousSelectExpression
                            .AddCrossJoin(selectExpression.Tables.Single(), selectExpression.Projection);

                        _queriesBySource.Remove(fromClause);

                        Expression
                            = new QueryFlatteningExpressionTreeVisitor(
                                previousQuerySource,
                                fromClause,
                                QueryCompilationContext,
                                readerOffset,
                                LinqOperatorProvider.SelectMany)
                                .VisitExpression(Expression);
                    }
                }
            }
        }

        public override void VisitJoinClause(JoinClause joinClause, QueryModel queryModel, int index)
        {
            var previousQuerySource
                = index == 0
                    ? queryModel.MainFromClause
                    : queryModel.BodyClauses[index - 1] as IQuerySource;

            var previousSelectExpression
                = previousQuerySource != null
                    ? TryGetQuery(previousQuerySource)
                    : null;

            var previousSelectProjectionCount
                = previousSelectExpression?.Projection.Count ?? -1;

            base.VisitJoinClause(joinClause, queryModel, index);

            if (previousSelectExpression != null)
            {
                var selectExpression = TryGetQuery(joinClause);

                if (selectExpression != null)
                {
                    var filteringExpressionTreeVisitor
                        = new FilteringExpressionTreeVisitor(this);

                    var predicate
                        = filteringExpressionTreeVisitor
                            .VisitExpression(
                                Expression.Equal(
                                    joinClause.OuterKeySelector,
                                    joinClause.InnerKeySelector));

                    if (predicate != null)
                    {
                        _queriesBySource.Remove(joinClause);

                        previousSelectExpression.RemoveRangeFromProjection(previousSelectProjectionCount);

                        var innerJoinExpression
                            = previousSelectExpression
                                .AddInnerJoin(
                                    selectExpression.Tables.Single(),
                                    QuerySourceRequiresMaterialization(joinClause)
                                        ? selectExpression.Projection
                                        : Enumerable.Empty<ColumnExpression>());

                        innerJoinExpression.Predicate = predicate;

                        Expression
                            = new QueryFlatteningExpressionTreeVisitor(
                                previousQuerySource,
                                joinClause,
                                QueryCompilationContext,
                                previousSelectProjectionCount,
                                LinqOperatorProvider.Join)
                                .VisitExpression(Expression);
                    }
                    else
                    {
                        previousSelectExpression.RemoveRangeFromProjection(previousSelectProjectionCount);
                    }
                }
            }
        }

        public override void VisitWhereClause(WhereClause whereClause, QueryModel queryModel, int index)
        {
            var previousExpression = Expression;

            var projectionCounts
                = _queriesBySource
                    .Select(kv => new
                        {
                            SelectExpression = kv.Value,
                            kv.Value.Projection.Count
                        })
                    .ToList();

            _requiresClientFilter = !_queriesBySource.Any();

            base.VisitWhereClause(whereClause, queryModel, index);

            foreach (var selectExpression in _queriesBySource.Values)
            {
                var filteringVisitor = new FilteringExpressionTreeVisitor(this);

                selectExpression.Predicate = filteringVisitor.VisitExpression(whereClause.Predicate);

                _requiresClientFilter |= filteringVisitor.RequiresClientEval;
            }

            if (!_requiresClientFilter)
            {
                foreach (var projectionCount in projectionCounts)
                {
                    projectionCount.SelectExpression
                        .RemoveRangeFromProjection(projectionCount.Count);
                }

                Expression = previousExpression;
            }
        }

        public override void VisitOrderByClause(OrderByClause orderByClause, QueryModel queryModel, int index)
        {
            if (_preOrderingExpression == null)
            {
                _preOrderingExpression = Expression;
            }

            var orderingCounts
                = _queriesBySource
                    .Where(kv => kv.Value.OrderBy.Any())
                    .Select(kv => new { kv.Key, kv.Value.OrderBy.Count })
                    .ToList();

            base.VisitOrderByClause(orderByClause, queryModel, index);

            foreach (var querySourceOrdering in orderingCounts)
            {
                var selectExpression = _queriesBySource[querySourceOrdering.Key];

                if (querySourceOrdering.Count != selectExpression.OrderBy.Count)
                {
                    var orderBy = selectExpression.OrderBy.ToList();

                    selectExpression.ClearOrderBy();
                    selectExpression.AddToOrderBy(orderBy.Skip(querySourceOrdering.Count));
                    selectExpression.AddToOrderBy(orderBy.Take(querySourceOrdering.Count));
                }
            }

            if (index == queryModel.BodyClauses.Count - 1)
            {
                var queriesWithOrdering
                    = _queriesBySource
                        .Where(kv => kv.Value.OrderBy.Any())
                        .Select(kv => kv.Value)
                        .ToList();

                if (queriesWithOrdering.Count == 1
                    && queriesWithOrdering[0].OrderBy.Count == queryModel.BodyClauses
                        .OfType<OrderByClause>()
                        .SelectMany(ob => ob.Orderings)
                        .Count())
                {
                    queriesWithOrdering[0].RemoveFromProjection(queriesWithOrdering[0].OrderBy);

                    Expression = _preOrderingExpression;
                }
                else
                {
                    foreach (var selectExpression in _queriesBySource.Values)
                    {
                        selectExpression.ClearOrderBy();
                    }
                }
            }
        }

        public override Expression BindMemberToValueReader(MemberExpression memberExpression, Expression expression)
        {
            Check.NotNull(memberExpression, "memberExpression");
            Check.NotNull(expression, "expression");

            return BindMemberExpression(
                memberExpression,
                (property, querySource, selectExpression) =>
                    {
                        var projectionIndex = selectExpression.GetProjectionIndex(property, querySource);

                        Debug.Assert(projectionIndex > -1);

                        return BindReadValueMethod(memberExpression.Type, expression, projectionIndex);
                    });
        }

        public override Expression BindMethodCallToValueReader(
            MethodCallExpression methodCallExpression, Expression expression)
        {
            Check.NotNull(methodCallExpression, "methodCallExpression");
            Check.NotNull(expression, "expression");

            return BindMethodCallExpression(
                methodCallExpression,
                (property, querySource, selectExpression) =>
                    {
                        var projectionIndex = selectExpression.GetProjectionIndex(property, querySource);

                        Debug.Assert(projectionIndex > -1);

                        return BindReadValueMethod(methodCallExpression.Type, expression, projectionIndex);
                    });
        }

        public virtual void BindMemberExpression(
            [NotNull] MemberExpression memberExpression,
            [NotNull] Action<IProperty, IQuerySource, SelectExpression> memberBinder)
        {
            Check.NotNull(memberExpression, "memberExpression");
            Check.NotNull(memberBinder, "memberBinder");

            BindMemberExpression(memberExpression, null,
                (property, querySource, selectExpression) =>
                    {
                        memberBinder(property, querySource, selectExpression);

                        return default(object);
                    });
        }

        public virtual TResult BindMemberExpression<TResult>(
            [NotNull] MemberExpression memberExpression,
            [NotNull] Func<IProperty, IQuerySource, SelectExpression, TResult> memberBinder)
        {
            Check.NotNull(memberExpression, "memberExpression");
            Check.NotNull(memberBinder, "memberBinder");

            return BindMemberExpression(memberExpression, null, memberBinder);
        }

        private TResult BindMemberExpression<TResult>(
            [NotNull] MemberExpression memberExpression,
            [CanBeNull] IQuerySource querySource,
            Func<IProperty, IQuerySource, SelectExpression, TResult> memberBinder)
        {
            Check.NotNull(memberExpression, "memberExpression");
            Check.NotNull(memberBinder, "memberBinder");

            return base.BindMemberExpression(memberExpression, querySource,
                (property, qs) => BindMemberOrMethod(memberBinder, qs, property));
        }

        public virtual void BindMethodCallExpression(
            [NotNull] MethodCallExpression methodCallExpression,
            [NotNull] Action<IProperty, IQuerySource, SelectExpression> memberBinder)
        {
            Check.NotNull(methodCallExpression, "methodCallExpression");
            Check.NotNull(memberBinder, "memberBinder");

            BindMethodCallExpression(methodCallExpression, null,
                (property, querySource, selectExpression) =>
                    {
                        memberBinder(property, querySource, selectExpression);

                        return default(object);
                    });
        }

        public virtual TResult BindMethodCallExpression<TResult>(
            [NotNull] MethodCallExpression methodCallExpression,
            [NotNull] Func<IProperty, IQuerySource, SelectExpression, TResult> memberBinder)
        {
            Check.NotNull(methodCallExpression, "methodCallExpression");
            Check.NotNull(memberBinder, "memberBinder");

            return BindMethodCallExpression(methodCallExpression, null, memberBinder);
        }

        private TResult BindMethodCallExpression<TResult>(
            MethodCallExpression methodCallExpression,
            IQuerySource querySource,
            Func<IProperty, IQuerySource, SelectExpression, TResult> memberBinder)
        {
            return base.BindMethodCallExpression(methodCallExpression, querySource,
                (property, qs) => BindMemberOrMethod(memberBinder, qs, property));
        }

        private TResult BindMemberOrMethod<TResult>(
            Func<IProperty, IQuerySource, SelectExpression, TResult> memberBinder,
            IQuerySource querySource,
            IProperty property)
        {
            var selectExpression = TryGetQuery(querySource);

            if (selectExpression != null)
            {
                return memberBinder(property, querySource, selectExpression);
            }

            selectExpression
                = _parentQueryModelVisitor?.TryGetQuery(querySource);

            selectExpression?
                .AddToProjection(
                    QueryCompilationContext.GetColumnName(property),
                    property,
                    querySource);

            return default(TResult);
        }

        public static readonly MethodInfo CreateValueReaderMethodInfo
            = typeof(RelationalQueryModelVisitor).GetTypeInfo()
                .GetDeclaredMethod("CreateValueReader");

        [UsedImplicitly]
        private static QuerySourceScope<IValueReader> CreateValueReader(
            IQuerySource querySource,
            QueryContext queryContext,
            QuerySourceScope parentQuerySourceScope,
            DbDataReader dataReader)
        {
            return new QuerySourceScope<IValueReader>(
                querySource,
                ((RelationalQueryContext)queryContext).ValueReaderFactory.Create(dataReader),
                parentQuerySourceScope);
        }

        public static readonly MethodInfo CreateEntityMethodInfo
            = typeof(RelationalQueryModelVisitor).GetTypeInfo()
                .GetDeclaredMethod("CreateEntity");

        [UsedImplicitly]
        private static QuerySourceScope<TEntity> CreateEntity<TEntity>(
            IQuerySource querySource,
            QueryContext queryContext,
            QuerySourceScope parentQuerySourceScope,
            DbDataReader dataReader,
            int readerOffset,
            IEntityType entityType,
            bool queryStateManager)
            where TEntity : class
        {
            var valueReader
                = ((RelationalQueryContext)queryContext).ValueReaderFactory
                    .Create(dataReader);

            if (readerOffset > 0)
            {
                valueReader = new OffsetValueReaderDecorator(valueReader, readerOffset);
            }

            return new QuerySourceScope<TEntity>(
                querySource,
                (TEntity)queryContext.QueryBuffer.GetEntity(entityType, valueReader, queryStateManager),
                parentQuerySourceScope);
        }
    }
}
