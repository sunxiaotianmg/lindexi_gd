﻿using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Markup;
using System.Xaml.Schema;

namespace System.Xaml.Demo
{
    public class XamlTypeInvokerOld
    {
        private static XamlTypeInvokerOld s_Unknown;
        private static object[] s_emptyObjectArray = Array.Empty<object>();

        private Dictionary<XamlType, MethodInfo> _addMethods;
        internal MethodInfo EnumeratorMethod { get; set; }
        private XamlType _xamlType;

        private Action<object> _constructorDelegate;

        private ThreeValuedBool _isPublic;

        // vvvvv---- Unused members.  Servicing policy is to retain these anyway.  -----vvvvv
        private ThreeValuedBool _isInSystemXaml;
        // ^^^^^----- End of unused members.  -----^^^^^

        protected XamlTypeInvokerOld()
        {
        }

        public XamlTypeInvokerOld(XamlType type)
        {
            _xamlType = type ?? throw new ArgumentNullException(nameof(type));
        }

        public static XamlTypeInvokerOld UnknownInvokerOld
        {
            get
            {
                if (s_Unknown == null)
                {
                    s_Unknown = new XamlTypeInvokerOld();
                }
                return s_Unknown;
            }
        }

        public EventHandler<XamlSetMarkupExtensionEventArgs> SetMarkupExtensionHandler
        {
            get { return _xamlType != null ? _xamlType.SetMarkupExtensionHandler : null; }
        }

        public EventHandler<XamlSetTypeConverterEventArgs> SetTypeConverterHandler
        {
            get { return _xamlType != null ? _xamlType.SetTypeConverterHandler : null; }
        }

        public virtual void AddToCollection(object instance, object item)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }
            IList list = instance as IList;
            if (list != null)
            {
                list.Add(item);
                return;
            }

            ThrowIfUnknown();
            if (!_xamlType.IsCollection)
            {
                throw new NotSupportedException(SR.Get(SRID.OnlySupportedOnCollections));
            }
            XamlType itemType;
            if (item != null)
            {
                itemType = _xamlType.SchemaContext.GetXamlType(item.GetType());
            }
            else
            {
                itemType = _xamlType.ItemType;
            }
            MethodInfo addMethod = GetAddMethod(itemType);
            if (addMethod == null)
            {
                throw new XamlSchemaException(SR.Get(SRID.NoAddMethodFound, _xamlType, itemType));
            }
            SafeReflectionInvoker.InvokeMethod(addMethod, instance, new object[] { item });
        }

        public virtual void AddToDictionary(object instance, object key, object item)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }
            IDictionary dictionary = instance as IDictionary;
            if (dictionary != null)
            {
                dictionary.Add(key, item);
                return;
            }

            ThrowIfUnknown();
            if (!_xamlType.IsDictionary)
            {
                throw new NotSupportedException(SR.Get(SRID.OnlySupportedOnDictionaries));
            }
            XamlType itemType;
            if (item != null)
            {
                itemType = _xamlType.SchemaContext.GetXamlType(item.GetType());
            }
            else
            {
                itemType = _xamlType.ItemType;
            }
            MethodInfo addMethod = GetAddMethod(itemType);
            if (addMethod == null)
            {
                throw new XamlSchemaException(SR.Get(SRID.NoAddMethodFound, _xamlType, itemType));
            }
            SafeReflectionInvoker.InvokeMethod(addMethod, instance, new object[] { key, item });
        }

        public virtual object CreateInstance(object[] arguments)
        {
            ThrowIfUnknown();
            if (!_xamlType.UnderlyingType.IsValueType && (arguments == null || arguments.Length == 0))
            {
                object result = DefaultCtorXamlActivator.CreateInstance(this);
                if (result != null)
                {
                    return result;
                }
            }
            return CreateInstanceWithActivator(_xamlType.UnderlyingType, arguments);
        }

        public virtual MethodInfo GetAddMethod(XamlType contentType)
        {
            if (contentType == null)
            {
                throw new ArgumentNullException(nameof(contentType));
            }
            if (IsUnknown || _xamlType.ItemType == null)
            {
                return null;
            }

            // Common case is that we match the item type. Short-circuit any additional lookup.
            if (contentType == _xamlType.ItemType ||
                (_xamlType.AllowedContentTypes.Count == 1 && contentType.CanAssignTo(_xamlType.ItemType)))
            {
                return _xamlType.AddMethod;
            }

            // Only collections can have additional content types
            if (!_xamlType.IsCollection)
            {
                return null;
            }

            // Populate the dictionary of all available Add methods
            MethodInfo addMethod;
            if (_addMethods == null)
            {
                Dictionary<XamlType, MethodInfo> addMethods = new Dictionary<XamlType, MethodInfo>();
                addMethods.Add(_xamlType.ItemType, _xamlType.AddMethod);
                foreach (XamlType type in _xamlType.AllowedContentTypes)
                {
                    addMethod = CollectionReflector.GetAddMethod(
                        _xamlType.UnderlyingType, type.UnderlyingType);
                    if (addMethod != null)
                    {
                        addMethods.Add(type, addMethod);
                    }
                }
                _addMethods = addMethods;
            }

            // First try the fast path.  Look for an exact match.
            if (_addMethods.TryGetValue(contentType, out addMethod))
            {
                return addMethod;
            }

            // Next the slow path.  Check each one for is assignable from.
            foreach (KeyValuePair<XamlType, MethodInfo> pair in _addMethods)
            {
                if (contentType.CanAssignTo(pair.Key))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        public virtual MethodInfo GetEnumeratorMethod()
        {
            return _xamlType.GetEnumeratorMethod;
        }

        public virtual IEnumerator GetItems(object instance)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }
            IEnumerable enumerable = instance as IEnumerable;
            if (enumerable != null)
            {
                return enumerable.GetEnumerator();
            }
            ThrowIfUnknown();
            if (!_xamlType.IsCollection && !_xamlType.IsDictionary)
            {
                throw new NotSupportedException(SR.Get(SRID.OnlySupportedOnCollectionsAndDictionaries));
            }
            MethodInfo getEnumMethod = GetEnumeratorMethod();
            return (IEnumerator)SafeReflectionInvoker.InvokeMethod(getEnumMethod, instance, s_emptyObjectArray);
        }

        // vvvvv---- Unused members.  Servicing policy is to retain these anyway.  -----vvvvv
        private bool IsInSystemXaml
        {
            [SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode", Justification = "Retained per servicing policy.")]
            get
            {
                if (_isInSystemXaml == ThreeValuedBool.NotSet)
                {
                    Type type = _xamlType.UnderlyingType.UnderlyingSystemType;
                    bool result = SafeReflectionInvoker.IsInSystemXaml(type);
                    _isInSystemXaml = result ? ThreeValuedBool.True : ThreeValuedBool.False;
                }
                return _isInSystemXaml == ThreeValuedBool.True;
            }
        }
        // ^^^^^----- End of unused members.  -----^^^^^

        private bool IsPublic
        {
            get
            {
                if (_isPublic == ThreeValuedBool.NotSet)
                {
                    Type type = _xamlType.UnderlyingType.UnderlyingSystemType;
                    _isPublic = type.IsVisible ? ThreeValuedBool.True : ThreeValuedBool.False;
                }
                return _isPublic == ThreeValuedBool.True;
            }
        }

        private bool IsUnknown
        {
            get { return _xamlType == null || _xamlType.UnderlyingType == null; }
        }

        private object CreateInstanceWithActivator(Type type, object[] arguments)
        {
            return SafeReflectionInvoker.CreateInstance(type, arguments);
        }

        private void ThrowIfUnknown()
        {
            if (IsUnknown)
            {
                throw new NotSupportedException(SR.Get(SRID.NotSupportedOnUnknownType));
            }
        }

        private static class DefaultCtorXamlActivator
        {
            private static ThreeValuedBool s_securityFailureWithCtorDelegate;
            private static ConstructorInfo s_actionCtor =
                typeof(Action<object>).GetConstructor(new Type[] { typeof(Object), typeof(IntPtr) });


            public static object CreateInstance(XamlTypeInvokerOld type)
            {
                if (!EnsureConstructorDelegate(type))
                {
                    return null;
                }
                object inst = CallCtorDelegate(type);
                return inst;
            }

#if TARGETTING35SP1
#else
#endif
            private static object CallCtorDelegate(XamlTypeInvokerOld type)
            {
                object inst = FormatterServices.GetUninitializedObject(type._xamlType.UnderlyingType);
                InvokeDelegate(type._constructorDelegate, inst);
                return inst;
            }

            private static void InvokeDelegate(Action<object> action, object argument)
            {
                action.Invoke(argument);
            }

#if TARGETTING35SP1
#else
#endif
            // returns true if a delegate is available, false if not
            private static bool EnsureConstructorDelegate(XamlTypeInvokerOld type)
            {
                if (type._constructorDelegate != null)
                {
                    return true;
                }
                if (!type.IsPublic)
                {
                    return false;
                }
                if (s_securityFailureWithCtorDelegate == ThreeValuedBool.NotSet)
                {
                    s_securityFailureWithCtorDelegate =
                        ThreeValuedBool.False;
                }
                if (s_securityFailureWithCtorDelegate == ThreeValuedBool.True)
                {
                    return false;
                }

                Type underlyingType = type._xamlType.UnderlyingType.UnderlyingSystemType;
                // Look up public ctors only, for equivalence with Activator.CreateInstance
                ConstructorInfo tConstInfo = underlyingType.GetConstructor(Type.EmptyTypes);
                if (tConstInfo == null)
                {
                    // Throwing MissingMethodException for equivalence with Activator.CreateInstance
                    throw new MissingMethodException(SR.Get(SRID.NoDefaultConstructor, underlyingType.FullName));
                }
                if ((tConstInfo.IsSecurityCritical && !tConstInfo.IsSecuritySafeCritical) ||
                    (tConstInfo.Attributes & MethodAttributes.HasSecurity) == MethodAttributes.HasSecurity ||
                    (underlyingType.Attributes & TypeAttributes.HasSecurity) == TypeAttributes.HasSecurity)
                {
                    // We don't want to bypass security checks for a critical or demanding ctor,
                    // so just treat it as if it were non-public
                    type._isPublic = ThreeValuedBool.False;
                    return false;
                }
                IntPtr constPtr = tConstInfo.MethodHandle.GetFunctionPointer();
                // This requires Reflection Permission
                Action<object> ctorDelegate = ctorDelegate =
                    (Action<object>)s_actionCtor.Invoke(new object[] { null, constPtr });
                type._constructorDelegate = ctorDelegate;
                return true;
            }
        }

        private class UnknownTypeInvokerOld : XamlTypeInvokerOld
        {
            public override void AddToCollection(object instance, object item)
            {
                throw new NotSupportedException(SR.Get(SRID.NotSupportedOnUnknownType));
            }

            public override void AddToDictionary(object instance, object key, object item)
            {
                throw new NotSupportedException(SR.Get(SRID.NotSupportedOnUnknownType));
            }

            public override object CreateInstance(object[] arguments)
            {
                throw new NotSupportedException(SR.Get(SRID.NotSupportedOnUnknownType));
            }

            public override IEnumerator GetItems(object instance)
            {
                throw new NotSupportedException(SR.Get(SRID.NotSupportedOnUnknownType));
            }
        }
    }
}