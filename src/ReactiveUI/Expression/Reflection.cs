﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using Splat;

namespace ReactiveUI
{
    /// <summary>
    /// Helper class for handling Reflection amd Expression tree related items.
    /// </summary>
    public static class Reflection
    {
        private static readonly ExpressionRewriter expressionRewriter = new ExpressionRewriter();

        private static readonly MemoizingMRUCache<string, Type> typeCache = new MemoizingMRUCache<string, Type>(
            (type, _) =>
            {
                return Type.GetType(type, false);
            }, 20);

        /// <summary>
        /// Uses the expression re-writer to simplify the Expression down to it's simplest Expression.
        /// </summary>
        /// <param name="expression">The expression to rewrite.</param>
        /// <returns>The rewritten expression.</returns>
        public static Expression Rewrite(Expression expression)
        {
            return expressionRewriter.Visit(expression);
        }

        /// <summary>
        /// Will convert a Expression which points towards a property
        /// to a string containing the property names.
        /// The sub-properties will be separated by the '.' character.
        /// Index based values will include [] after the name.
        /// </summary>
        /// <param name="expression">The expression to generate the property names from.</param>
        /// <returns>A string form for the property the expression is pointing to.</returns>
        public static string ExpressionToPropertyNames(Expression expression)
        {
            Contract.Requires(expression != null);

            StringBuilder sb = new StringBuilder();

            foreach (var exp in expression.GetExpressionChain())
            {
                if (exp.NodeType != ExpressionType.Parameter)
                {
                    // Indexer expression
                    if (exp.NodeType == ExpressionType.Index)
                    {
                        var ie = (IndexExpression)exp;
                        sb.Append(ie.Indexer.Name);
                        sb.Append('[');

                        foreach (var argument in ie.Arguments)
                        {
                            sb.Append(((ConstantExpression)argument).Value);
                            sb.Append(',');
                        }

                        sb.Replace(',', ']', sb.Length - 1, 1);
                    }
                    else if (exp.NodeType == ExpressionType.MemberAccess)
                    {
                        var me = (MemberExpression)exp;
                        sb.Append(me.Member.Name);
                    }
                }

                sb.Append('.');
            }

            if (sb.Length > 0)
            {
                sb.Remove(sb.Length - 1, 1);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Converts a MemberInfo into a Func which will fetch the value for the Member.
        /// Handles either fields or properties.
        /// </summary>
        /// <param name="member">The member info to convert.</param>
        /// <returns>A Func that takes in the object/indexes and returns the value.</returns>
        public static Func<object, object[], object> GetValueFetcherForProperty(MemberInfo member)
        {
            Contract.Requires(member != null);

            FieldInfo field = member as FieldInfo;
            if (field != null)
            {
                return (obj, args) => field.GetValue(obj);
            }

            PropertyInfo property = member as PropertyInfo;
            if (property != null)
            {
                return property.GetValue;
            }

            return null;
        }

        /// <summary>
        /// Converts a MemberInfo into a Func which will fetch the value for the Member.
        /// Handles either fields or properties.
        /// If there is no field or property with the matching MemberInfo it'll throw
        /// an ArgumentException.
        /// </summary>
        /// <param name="member">The member info to convert.</param>
        /// <returns>A Func that takes in the object/indexes and returns the value.</returns>
        public static Func<object, object[], object> GetValueFetcherOrThrow(MemberInfo member)
        {
            var ret = GetValueFetcherForProperty(member);

            if (ret == null)
            {
                throw new ArgumentException(string.Format("Type '{0}' must have a property '{1}'", member.DeclaringType, member.Name));
            }

            return ret;
        }

        /// <summary>
        /// Converts a MemberInfo into a Func which will set the value for the Member.
        /// Handles either fields or properties.
        /// If there is no field or property with the matching MemberInfo it'll throw
        /// an ArgumentException.
        /// </summary>
        /// <param name="member">The member info to convert.</param>
        /// <returns>A Func that takes in the object/indexes and sets the value.</returns>
        public static Action<object, object, object[]> GetValueSetterForProperty(MemberInfo member)
        {
            Contract.Requires(member != null);

            FieldInfo field = member as FieldInfo;
            if (field != null)
            {
                return (obj, val, args) => field.SetValue(obj, val);
            }

            PropertyInfo property = member as PropertyInfo;
            if (property != null)
            {
                return property.SetValue;
            }

            return null;
        }

        /// <summary>
        /// Converts a MemberInfo into a Func which will set the value for the Member.
        /// Handles either fields or properties.
        /// If there is no field or property with the matching MemberInfo it'll throw
        /// an ArgumentException.
        /// </summary>
        /// <param name="member">The member info to convert.</param>
        /// <returns>A Func that takes in the object/indexes and sets the value.</returns>
        public static Action<object, object, object[]> GetValueSetterOrThrow(MemberInfo member)
        {
            var ret = GetValueSetterForProperty(member);

            if (ret == null)
            {
                throw new ArgumentException($"Type '{member.DeclaringType}' must have a property '{member.Name}'");
            }

            return ret;
        }

        /// <summary>
        /// Based on a list of Expressions get the value of the last property in the chain if possible.
        /// The Expressions are typically property chains. Eg Property1.Property2.Property3
        /// The method will make sure that each Expression can get a value along the way
        /// and get each property until each expression is evaluated.
        /// </summary>
        /// <param name="changeValue">A output value where to store the value if the value can be fetched.</param>
        /// <param name="current">The object that starts the property chain.</param>
        /// <param name="expressionChain">A list of expressions which will point towards a property or field.</param>
        /// <typeparam name="TValue">The type of the end value we are trying to get.</typeparam>
        /// <returns>If the value was successfully retrieved or not.</returns>
        public static bool TryGetValueForPropertyChain<TValue>(out TValue changeValue, object current, IEnumerable<Expression> expressionChain)
        {
            foreach (var expression in expressionChain.SkipLast(1))
            {
                if (current == null)
                {
                    changeValue = default(TValue);
                    return false;
                }

                current = GetValueFetcherOrThrow(expression.GetMemberInfo())(current, expression.GetArgumentsArray());
            }

            if (current == null)
            {
                changeValue = default(TValue);
                return false;
            }

            Expression lastExpression = expressionChain.Last();
            changeValue = (TValue)GetValueFetcherOrThrow(lastExpression.GetMemberInfo())(current, lastExpression.GetArgumentsArray());
            return true;
        }

        /// <summary>
        /// Based on a list of Expressions get a IObservedChanged for the value
        /// of the last property in the chain if possible.
        /// The Expressions are property chains. Eg Property1.Property2.Property3
        /// The method will make sure that each Expression can get a value along the way
        /// and get each property until each expression is evaluated.
        /// </summary>
        /// <param name="changeValues">A IObservedChanged for the value.</param>
        /// <param name="current">The object that starts the property chain.</param>
        /// <param name="expressionChain">A list of expressions which will point towards a property or field.</param>
        /// <returns>If the value was successfully retrieved or not.</returns>
        public static bool TryGetAllValuesForPropertyChain(out IObservedChange<object, object>[] changeValues, object current, IEnumerable<Expression> expressionChain)
        {
            int currentIndex = 0;
            changeValues = new IObservedChange<object, object>[expressionChain.Count()];

            foreach (var expression in expressionChain.SkipLast(1))
            {
                if (current == null)
                {
                    changeValues[currentIndex] = null;
                    return false;
                }

                var sender = current;
                current = GetValueFetcherOrThrow(expression.GetMemberInfo())(current, expression.GetArgumentsArray());
                var box = new ObservedChange<object, object>(sender, expression, current);

                changeValues[currentIndex] = box;
                currentIndex++;
            }

            if (current == null)
            {
                changeValues[currentIndex] = null;
                return false;
            }

            Expression lastExpression = expressionChain.Last();
            changeValues[currentIndex] = new ObservedChange<object, object>(current, lastExpression, GetValueFetcherOrThrow(lastExpression.GetMemberInfo())(current, lastExpression.GetArgumentsArray()));

            return true;
        }

        public static bool TrySetValueToPropertyChain<TValue>(object target, IEnumerable<Expression> expressionChain, TValue value, bool shouldThrow = true)
        {
            foreach (var expression in expressionChain.SkipLast(1))
            {
                var getter = shouldThrow ?
                    GetValueFetcherOrThrow(expression.GetMemberInfo()) :
                    GetValueFetcherForProperty(expression.GetMemberInfo());

                target = getter(target, expression.GetArgumentsArray());
            }

            if (target == null)
            {
                return false;
            }

            Expression lastExpression = expressionChain.Last();
            var setter = shouldThrow ?
                GetValueSetterOrThrow(lastExpression.GetMemberInfo()) :
                GetValueSetterForProperty(lastExpression.GetMemberInfo());

            if (setter == null)
            {
                return false;
            }

            setter(target, value, lastExpression.GetArgumentsArray());
            return true;
        }

        /// <summary>
        /// Gets a 
        /// </summary>
        /// <param name="type"></param>
        /// <param name="throwOnFailure"></param>
        /// <returns></returns>
        /// <exception cref="TypeLoadException"></exception>
        public static Type ReallyFindType(string type, bool throwOnFailure)
        {
            lock (typeCache)
            {
                var ret = typeCache.Get(type);
                if (ret != null || !throwOnFailure)
                {
                    return ret;
                }

                throw new TypeLoadException();
            }
        }

        public static Type GetEventArgsTypeForEvent(Type type, string eventName)
        {
            var ti = type;
            var ei = ti.GetRuntimeEvent(eventName);
            if (ei == null)
            {
                throw new Exception(string.Format("Couldn't find {0}.{1}", type.FullName, eventName));
            }

            // Find the EventArgs type parameter of the event via digging around via reflection
            var eventArgsType = ei.EventHandlerType.GetRuntimeMethods().First(x => x.Name == "Invoke").GetParameters()[1].ParameterType;
            return eventArgsType;
        }

        /// <summary>
        /// Checks to make sure that the specified method names on the target object
        /// are overriden.
        /// </summary>
        /// <param name="callingTypeName">The name of the calling type.</param>
        /// <param name="targetObject">The object to check.</param>
        /// <param name="methodsToCheck">The name of the methods to check.</param>
        /// <exception cref="Exception">Thrown if the methods aren't overriden on the target object.</exception>
        public static void ThrowIfMethodsNotOverloaded(string callingTypeName, object targetObject, params string[] methodsToCheck)
        {
            var missingMethod = methodsToCheck
                .Select(x =>
                {
                    var methods = targetObject.GetType().GetTypeInfo().DeclaredMethods;
                    return Tuple.Create(x, methods.FirstOrDefault(y => y.Name == x));
                })
                .FirstOrDefault(x => x.Item2 == null);

            if (missingMethod != null)
            {
                throw new Exception(string.Format("Your class must implement {0} and call {1}.{0}", missingMethod.Item1, callingTypeName));
            }
        }

        /// <summary>
        /// Determines if the specified property is static or not.
        /// </summary>
        /// <param name="this">The property information to check.</param>
        /// <returns>If the property is static or not.</returns>
        public static bool IsStatic(this PropertyInfo @this)
        {
            return (@this.GetMethod ?? @this.SetMethod).IsStatic;
        }

        internal static IObservable<object> ViewModelWhenAnyValue<TView, TViewModel>(TViewModel viewModel, TView view, Expression expression)
            where TView : IViewFor
            where TViewModel : class
        {
            return view.WhenAnyValue(x => x.ViewModel)
                .Where(x => x != null)
                .Select(x => ((TViewModel)x).WhenAnyDynamic(expression, y => y.Value))
                .Switch();
        }
    }
}
