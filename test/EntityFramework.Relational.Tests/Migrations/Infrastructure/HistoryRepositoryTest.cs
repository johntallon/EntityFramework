﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Microsoft.Data.Entity.Infrastructure;
using Microsoft.Data.Entity.Relational.Migrations.Infrastructure;
using Microsoft.Data.Entity.Relational.Update;
using Microsoft.Data.Entity.Tests;
using Microsoft.Framework.DependencyInjection;
using Moq;
using Moq.Protected;
using Xunit;

namespace Microsoft.Data.Entity.Relational.Tests.Migrations.Infrastructure
{
    public class HistoryRepositoryTest
    {
        [Fact]
        public void Get_table_name()
        {
            var serviceProvider = TestHelpers.CreateServiceProvider();

            using (var context = new Context(serviceProvider))
            {
                var historyRepository = new HistoryRepository(
                    serviceProvider,
                    new DbContextService<IDbContextOptions>(new DbContextOptions()),
                    new DbContextService<DbContext>(context));

                Assert.Equal("__MigrationHistory", historyRepository.TableName);
            }
        }

        [Fact]
        public void Create_and_cache_history_model()
        {
            var serviceProvider = TestHelpers.CreateServiceProvider();

            using (var context = new Context(serviceProvider))
            {
                var historyRepository = new HistoryRepository(
                    serviceProvider,
                    new DbContextService<IDbContextOptions>(new DbContextOptions()),
                    new DbContextService<DbContext>(context));

                var historyModel1 = historyRepository.HistoryModel;
                var historyModel2 = historyRepository.HistoryModel;

                Assert.Same(historyModel1, historyModel2);
                Assert.Equal(1, historyModel1.EntityTypes.Count);

                var entityType = historyModel1.EntityTypes[0];
                Assert.Equal("Microsoft.Data.Entity.Relational.Infrastructure.HistoryRow", entityType.Name);
                Assert.Equal(3, entityType.Properties.Count);
                Assert.Equal(new[] { "ContextKey", "MigrationId", "ProductVersion" }, entityType.Properties.Select(p => p.Name));

                Assert.Equal(150, entityType.Properties.Single(p => p.Name == "MigrationId").MaxLength);
                Assert.Equal(300, entityType.Properties.Single(p => p.Name == "ContextKey").MaxLength);
                Assert.Equal(32, entityType.Properties.Single(p => p.Name == "ProductVersion").MaxLength);
            }
        }

        [Fact]
        public void Create_history_context_from_user_context()
        {
            var serviceProvider = TestHelpers.CreateServiceProvider();

            using (var context = new Context(serviceProvider))
            {
                var historyRepository = new HistoryRepository(
                    serviceProvider,
                    new DbContextService<IDbContextOptions>(new DbContextOptions()),
                    new DbContextService<DbContext>(context));

                using (var historyContext = historyRepository.CreateHistoryContext())
                {
                    Assert.Same(historyRepository.HistoryModel, historyContext.Model);

                    var options = ((IDbContextServices)context).ScopedServiceProvider.GetRequiredService<DbContextService<IDbContextOptions>>();
                    var historyOptions = ((IDbContextServices)historyContext).ScopedServiceProvider.GetRequiredService<DbContextService<IDbContextOptions>>();

                    var extensions = options.Service.Extensions;
                    var historyExtensions = historyOptions.Service.Extensions;

                    Assert.Equal(extensions.Count, historyExtensions.Count);

                    for (var i = 0; i < extensions.Count; i++)
                    {
                        Assert.Same(extensions[i], historyExtensions[i]);
                    }
                }
            }
        }

        [Fact]
        public void Get_migrations_query()
        {
            var serviceProvider = TestHelpers.CreateServiceProvider();

            using (var context = new Context(serviceProvider))
            {
                var historyRepository = new HistoryRepository(
                    serviceProvider,
                    new DbContextService<IDbContextOptions>(new DbContextOptions()),
                    new DbContextService<DbContext>(context));

                using (var historyContext = historyRepository.CreateHistoryContext())
                {
                    var query = historyRepository.GetMigrationsQuery(historyContext);

                    var expression = (MethodCallExpression)query.Expression;

                    Assert.Equal("OrderBy", expression.Method.Name);
                    Assert.Equal("h => h.MigrationId", expression.Arguments[1].ToString());

                    expression = (MethodCallExpression)expression.Arguments[0];

                    Assert.Equal("Where", expression.Method.Name);
                    Assert.Equal(
                        "h => (h.ContextKey == value(Microsoft.Data.Entity.Relational.Infrastructure.HistoryRepository).GetContextKey())",
                        expression.Arguments[1].ToString());

                    var queryableType = expression.Arguments[0].Type;

                    Assert.True(queryableType.IsGenericType);
                    Assert.Equal("EntityQueryable", queryableType.Name.Remove(queryableType.Name.IndexOf("`", StringComparison.Ordinal)));
                    Assert.Equal(1, queryableType.GenericTypeArguments.Length);
                    Assert.Equal("HistoryRow", queryableType.GenericTypeArguments[0].Name);
                }
            }
        }

        [Fact]
        public void Get_migrations()
        {
            var serviceProvider = TestHelpers.CreateServiceProvider();

            using (var context = new Context(serviceProvider))
            {
                var historyRepositoryMock = new Mock<HistoryRepository>(
                    serviceProvider,
                    new DbContextService<IDbContextOptions>(new DbContextOptions()),
                    new DbContextService<DbContext>(context))
                { CallBase = true };

                historyRepositoryMock
                    .Setup(o => o.GetMigrationsQuery(It.IsAny<DbContext>()))
                    .Returns(MigrationQueryableCallback);

                var historyRows = historyRepositoryMock.Object.Rows;
                Assert.Equal(2, historyRows.Count);
                Assert.Equal("000000000000001_Migration1", historyRows[0].MigrationId);
                Assert.Equal("000000000000002_Migration2", historyRows[1].MigrationId);
            }
        }

        private static IQueryable<HistoryRow> MigrationQueryableCallback()
        {
            return new[]
                {
                    new HistoryRow { MigrationId = "000000000000001_Migration1" },
                    new HistoryRow { MigrationId = "000000000000002_Migration2" }
                }
                .AsQueryable();
        }

        [Fact]
        public void Generate_insert_migration_sql()
        {
            var serviceProvider = TestHelpers.CreateServiceProvider();

            using (var context = new Context(serviceProvider))
            {
                var historyRepository = new HistoryRepository(
                    serviceProvider,
                    new DbContextService<IDbContextOptions>(new DbContextOptions()),
                    new DbContextService<DbContext>(context));

                var sqlBatches = historyRepository.GenerateInsertMigrationSql(
                    new MigrationInfo("000000000000001_Foo"), new DmlSqlGenerator()).ToList();

                Assert.Equal(1, sqlBatches.Count);
                Assert.Equal(string.Format(
                    @"INSERT INTO ""__MigrationHistory"" (""MigrationId"", ""ContextKey"", ""ProductVersion"") VALUES ('000000000000001_Foo', 'Microsoft.Data.Entity.Relational.Tests.Infrastructure.HistoryRepositoryTest+Context', '{0}')",
                    MigrationInfo.CurrentProductVersion), sqlBatches[0].Sql);
            }
        }

        [Fact]
        public void Generate_insert_migration_sql_with_custom_context_key()
        {
            var serviceProvider = TestHelpers.CreateServiceProvider();

            using (var context = new Context(serviceProvider))
            {
                var historyRepository = new Mock<HistoryRepository>(
                    serviceProvider,
                    new DbContextService<IDbContextOptions>(new DbContextOptions()),
                    new DbContextService<DbContext>(context))
                { CallBase = true };

                historyRepository.Protected().Setup<string>("GetContextKey").Returns("SomeContextKey");

                var sqlBatches = historyRepository.Object.GenerateInsertMigrationSql(
                    new MigrationInfo("000000000000001_Foo"), new DmlSqlGenerator()).ToList();

                Assert.Equal(1, sqlBatches.Count);
                Assert.Equal(string.Format(
                    @"INSERT INTO ""__MigrationHistory"" (""MigrationId"", ""ContextKey"", ""ProductVersion"") VALUES ('000000000000001_Foo', 'SomeContextKey', '{0}')",
                    MigrationInfo.CurrentProductVersion), sqlBatches[0].Sql);
            }
        }

        [Fact]
        public void Generate_delete_migration_sql()
        {
            var serviceProvider = TestHelpers.CreateServiceProvider();

            using (var context = new Context(serviceProvider))
            {
                var historyRepository = new HistoryRepository(
                    serviceProvider,
                    new DbContextService<IDbContextOptions>(new DbContextOptions()),
                    new DbContextService<DbContext>(context));

                var sqlBatches = historyRepository.GenerateDeleteMigrationSql(
                    new MigrationInfo("000000000000001_Foo"), new DmlSqlGenerator()).ToList();

                Assert.Equal(1, sqlBatches.Count);
                Assert.Equal(
                    @"DELETE FROM ""__MigrationHistory"" WHERE ""MigrationId"" = '000000000000001_Foo' AND ""ContextKey"" = 'Microsoft.Data.Entity.Relational.Tests.Infrastructure.HistoryRepositoryTest+Context'",
                    sqlBatches[0].Sql);
            }
        }

        #region Fixture

        public class Context : DbContext
        {
            public Context(IServiceProvider serviceProvider)
                : base(serviceProvider)
            {
            }
        }

        public class DmlSqlGenerator : SqlGenerator
        {
            protected override void AppendIdentityWhereCondition(StringBuilder commandStringBuilder, ColumnModification columnModification)
            {
                throw new NotImplementedException();
            }

            public override void AppendSelectAffectedCountCommand(StringBuilder commandStringBuilder, SchemaQualifiedName schemaQualifiedName)
            {
                throw new NotImplementedException();
            }

            protected override void AppendRowsAffectedWhereCondition(StringBuilder commandStringBuilder, int expectedRowsAffected)
            {
                throw new NotImplementedException();
            }
        }

        #endregion
    }
}
