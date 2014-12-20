// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Data.Entity.Metadata;
using Microsoft.Data.Entity.Metadata.Internal;
using Xunit;

namespace Microsoft.Data.Entity.Tests.Metadata.Internal
{
    public class InternalRelationshipBuilderTest
    {
        [Fact]
        public void ForeignKey_returns_same_instance_if_no_navigations()
        {
            var modelBuilder = new InternalModelBuilder(new Model(), null);
            var customerEntityBuilder = modelBuilder.Entity(typeof(Customer), ConfigurationSource.Explicit);
            customerEntityBuilder.Key(new[] { Customer.IdProperty, Customer.UniqueProperty }, ConfigurationSource.Explicit);
            var orderEntityBuilder = modelBuilder.Entity(typeof(Order), ConfigurationSource.Explicit);
            orderEntityBuilder.Key(new[] { Order.IdProperty }, ConfigurationSource.Explicit);

            var relationshipBuilder = orderEntityBuilder.Relationship(typeof(Customer), typeof(Order), null, null, /*oneToOne:*/ true, ConfigurationSource.Convention)
                .ForeignKey(typeof(Order), new[] { Order.CustomerIdProperty.Name, Order.CustomerUniqueProperty.Name }, ConfigurationSource.Convention);

            Assert.NotNull(relationshipBuilder);
            Assert.Same(relationshipBuilder, customerEntityBuilder.Relationship(typeof(Order), typeof(Customer), null, null, /*oneToOne:*/ true, ConfigurationSource.Convention)
                .ForeignKey(typeof(Order).FullName, new[] { Order.CustomerIdProperty.Name, Order.CustomerUniqueProperty.Name }, ConfigurationSource.Convention));
        }

        [Fact]
        public void ReferencedKey_does_not_return_same_instance_if_no_navigations_or_foreign_key()
        {
            var modelBuilder = new InternalModelBuilder(new Model(), null);
            var customerEntityBuilder = modelBuilder.Entity(typeof(Customer), ConfigurationSource.Explicit);
            customerEntityBuilder.Key(new[] { Customer.IdProperty, Customer.UniqueProperty }, ConfigurationSource.Explicit);
            var orderEntityBuilder = modelBuilder.Entity(typeof(Order), ConfigurationSource.Explicit);
            orderEntityBuilder.Key(new[] { Order.IdProperty }, ConfigurationSource.Explicit);

            var relationshipBuilder = orderEntityBuilder.Relationship(typeof(Customer), typeof(Order), null, null, /*oneToOne:*/ true, ConfigurationSource.Convention)
                .ReferencedKey(typeof(Order), new[] { Order.CustomerIdProperty.Name, Order.CustomerUniqueProperty.Name }, ConfigurationSource.Convention);

            Assert.NotNull(relationshipBuilder);
            Assert.NotSame(relationshipBuilder, customerEntityBuilder.Relationship(typeof(Order), typeof(Customer), null, null, /*oneToOne:*/ true, ConfigurationSource.Convention)
                .ReferencedKey(typeof(Order).FullName, new[] { Order.CustomerIdProperty.Name, Order.CustomerUniqueProperty.Name }, ConfigurationSource.Convention));

            Assert.Equal(2, customerEntityBuilder.Metadata.ForeignKeys.Count);
        }

        // Unique
        // Required
        private class Order
        {
            public static readonly PropertyInfo IdProperty = typeof(Order).GetProperty("Id");
            public static readonly PropertyInfo CustomerIdProperty = typeof(Order).GetProperty("CustomerId");
            public static readonly PropertyInfo CustomerUniqueProperty = typeof(Order).GetProperty("CustomerUnique");
            public static readonly PropertyInfo CustomerProperty = typeof(Order).GetProperty("Customer");

            public int Id { get; set; }
            public int CustomerId { get; set; }
            public Guid CustomerUnique { get; set; }
            public Customer Customer { get; set; }

            public Order OrderCustomer { get; set; }
        }

        private class Customer
        {
            public static readonly PropertyInfo IdProperty = typeof(Customer).GetProperty("Id");
            public static readonly PropertyInfo NameProperty = typeof(Customer).GetProperty("Name");
            public static readonly PropertyInfo UniqueProperty = typeof(Customer).GetProperty("Unique");
            public static readonly PropertyInfo OrdersProperty = typeof(Customer).GetProperty("Orders");

            public int Id { get; set; }
            public Guid Unique { get; set; }
            public string Name { get; set; }
            public string Mane { get; set; }
            public ICollection<Order> Orders { get; set; }

            public IEnumerable<Order> EnumerableOrders { get; set; }
            public Order NotCollectionOrders { get; set; }
        }
    }
}
