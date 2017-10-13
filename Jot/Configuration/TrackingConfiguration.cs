﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Linq.Expressions;
using System.ComponentModel;
using Jot.Triggers;

namespace Jot.Configuration
{
    public sealed class TrackingConfiguration
    {
        public StateTracker StateTracker { get; private set; }

        public string Key { get; set; }
        public Dictionary<string, TrackedPropertyDescriptor> TrackedProperties { get; set; }//todo: add DefaultValue property to trackable attribute
        public WeakReference TargetReference { get; private set; }
        public bool AutoPersistEnabled { get; set; }

        #region apply/persist events

        public event EventHandler<TrackingOperationEventArgs> ApplyingProperty;
        private object OnApplyingState(string property, object value)
        {
            var handler = ApplyingProperty;
            if (handler != null)
            {
                TrackingOperationEventArgs args = new TrackingOperationEventArgs(this, property, value);
                handler(this, args);

                if (args.Cancel)
                    throw new OperationCanceledException();

                return args.Value;
            }
            else
                return value;
        }

        public event EventHandler StateApplied;
        private void OnStateApplied()
        {
            StateApplied?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler<TrackingOperationEventArgs> PersistingProperty;
        private object OnPersistingState(string property, object value)
        {
            var handler = PersistingProperty;
            if (handler != null)
            {
                TrackingOperationEventArgs args = new TrackingOperationEventArgs(this, property, value);
                handler(this, args);
                if (args.Cancel)
                    throw new OperationCanceledException();
                else
                    return args.Value;
            }
            return value;
        }

        public event EventHandler StatePersisted;
        private void OnStatePersisted()
        {
            StatePersisted?.Invoke(this, EventArgs.Empty);
        }
        #endregion

        internal TrackingConfiguration(object target, StateTracker tracker)
        {
            StateTracker = tracker;
            this.TargetReference = new WeakReference(target);
            TrackedProperties = new Dictionary<string, TrackedPropertyDescriptor>();
            AutoPersistEnabled = true;
            AddMetaData();

            ITrackingAware trackingAwareTarget = target as ITrackingAware;
            if (trackingAwareTarget != null)
                trackingAwareTarget.InitConfiguration(this);

            ITriggerPersist asNotify = target as ITriggerPersist;
            if (asNotify != null)
                asNotify.PersistRequired += (s, e) => Persist();
        }

        public void Persist()
        {
            if (TargetReference.IsAlive)
            {
                foreach (string propertyName in TrackedProperties.Keys)
                {
                    var value = TrackedProperties[propertyName].Getter(TargetReference.Target);

                    try
                    {
                        value = OnPersistingState(propertyName, value);
                        StateTracker.ObjectStore.Persist(value, ConstructPropertyKey(propertyName));
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine(string.Format("Persisting failed, property key = '{0}', message='{1}'.", ConstructPropertyKey(propertyName), ex.Message));
                    }
                }

                OnStatePersisted();
            }
        }

        bool _applied = false;
        public void Apply()
        {
            if (TargetReference.IsAlive)
            {
#if DEBUG
                Stopwatch sw = new Stopwatch();
                sw.Start();
#endif
                foreach (string propertyName in TrackedProperties.Keys)
                {
                    string key = ConstructPropertyKey(propertyName);
                    TrackedPropertyDescriptor descriptor = TrackedProperties[propertyName];

                    if (StateTracker.ObjectStore.ContainsKey(key))
                    {
                        try
                        {
                            object storedValue = StateTracker.ObjectStore.Retrieve(key);
                            object valueToApply = OnApplyingState(propertyName, storedValue);
                            descriptor.Setter(TargetReference.Target, storedValue);
                        }
                        catch (OperationCanceledException)
                        { }
                        catch (Exception ex)
                        {
                            Trace.WriteLine(string.Format("TRACKING: Applying tracking to property with key='{0}' failed. ExceptionType:'{1}', message: '{2}'!", key, ex.GetType().Name, ex.Message));
                            if(descriptor.IsDefaultSpecified)
                                descriptor.Setter(TargetReference.Target, descriptor.DefaultValue);
                        }
                    }
                    else if (descriptor.IsDefaultSpecified)
                    {
                        descriptor.Setter(TargetReference.Target, descriptor.DefaultValue);
                    }

#if DEBUG
                    sw.Stop();
                    Trace.WriteLine($"Applied property {propertyName} of target {TargetReference.Target} in {sw.ElapsedMilliseconds}ms");
                    sw.Reset();
#endif
				}

				OnStateApplied();
            }
            _applied = true;
        }

        public TrackingConfiguration AddProperties(params string[] properties)
        {
            foreach (string property in properties)
                TrackedProperties[property] = CreateDescriptor(property, false, null);
            return this;
        }
        public TrackingConfiguration AddProperties<T>(params Expression<Func<T, object>>[] properties)
        {
            AddProperties(properties.Select(p => GetPropertyNameFromExpression(p)).ToArray());
            return this;
        }

        public TrackingConfiguration AddProperty<T>(Expression<Func<T, object>> property, object defaultValue)
        {
            AddProperty(GetPropertyNameFromExpression(property), defaultValue);
            return this;
        }
        public TrackingConfiguration AddProperty<T>(Expression<Func<T, object>> property)
        {
            AddProperty(GetPropertyNameFromExpression(property));
            return this;
        }

        public TrackingConfiguration AddProperty(string property, object defaultValue)
        {
            TrackedProperties[property] = CreateDescriptor(property, true, defaultValue);
            return this;
        }

        public TrackingConfiguration AddProperty(string property)
        {
            TrackedProperties[property] = CreateDescriptor(property, false, null);
            return this;
        }

        public TrackingConfiguration RemoveProperties(params string[] properties)
        {
            foreach (string property in properties)
                TrackedProperties.Remove(property);
            return this;
        }
        public TrackingConfiguration RemoveProperties<T>(params Expression<Func<T, object>>[] properties)
        {
            RemoveProperties(properties.Select(p => GetPropertyNameFromExpression(p)).ToArray());
            return this;
        }

        public TrackingConfiguration RegisterPersistTrigger(string eventName)
        {
            return RegisterPersistTrigger(eventName, TargetReference.Target);
        }

        public TrackingConfiguration RegisterPersistTrigger(string eventName, object eventSourceObject)
        {
            EventInfo eventInfo = eventSourceObject.GetType().GetEvent(eventName);
            var parameters = eventInfo.EventHandlerType
                .GetMethod("Invoke")
                .GetParameters()
                .Select(parameter => Expression.Parameter(parameter.ParameterType))
                .ToArray();

            var handler = Expression.Lambda(
                    eventInfo.EventHandlerType,
                    Expression.Call(Expression.Constant(new Action(() => { if (_applied) Persist(); })), "Invoke", Type.EmptyTypes),
                    parameters)
              .Compile();

            eventInfo.AddEventHandler(eventSourceObject, handler);
            return this;
        }

        public TrackingConfiguration IdentifyAs(string key)
        {
            this.Key = key;
            return this;
        }

        public TrackingConfiguration SetAutoPersistEnabled(bool shouldAutoPersist)
        {
            AutoPersistEnabled = shouldAutoPersist;
            return this;
        }

        private TrackingConfiguration AddMetaData()
        {
            Type t = TargetReference.Target.GetType();

            PropertyInfo keyProperty = t.GetProperties().SingleOrDefault(pi => pi.IsDefined(typeof(TrackingKeyAttribute), true));
            if (keyProperty != null)
                Key = keyProperty.GetValue(TargetReference.Target, null).ToString();

            foreach (PropertyInfo pi in t.GetProperties())
            {
                //don't track the key property
                if (pi == keyProperty)
                    continue;

                TrackableAttribute propTrackableAtt = pi.GetCustomAttributes(true).OfType<TrackableAttribute>().Where(ta => ta.TrackerName == StateTracker.Name).SingleOrDefault();
                if (propTrackableAtt != null)
                {
                    DefaultValueAttribute defaultAtt = pi.CustomAttributes.OfType<DefaultValueAttribute>().SingleOrDefault();
                    if (defaultAtt != null)
                        TrackedProperties[pi.Name] = CreateDescriptor(pi.Name, true, defaultAtt.Value);
                    else
                        TrackedProperties[pi.Name] = CreateDescriptor(pi.Name, false, null);
                }
            }
            return this;
        }

        private static string GetPropertyNameFromExpression<T>(Expression<Func<T, object>> exp)
        {
            MemberExpression membershipExpression;
            if (exp.Body is UnaryExpression)
                membershipExpression = (exp.Body as UnaryExpression).Operand as MemberExpression;
            else
                membershipExpression = exp.Body as MemberExpression;
            return membershipExpression.Member.Name;
        }

        public TrackingConfiguration ClearSavedState()
        {
            foreach (string propertyName in TrackedProperties.Keys)
            {
                string key = ConstructPropertyKey(propertyName);
                try
                {
                    StateTracker.ObjectStore.Remove(key);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(string.Format("TRACKING: Failed to delete property '{0}'. ExceptionType:'{1}', message: '{2}'!", key, ex.GetType().Name, ex.Message));
                }
            }
            return this;
        }

        private TrackedPropertyDescriptor CreateDescriptor(string propertyName, bool isDefaultSpecifier, object defaultValue)
        {
            PropertyInfo pi = TargetReference.Target.GetType().GetProperty(propertyName);
            return new TrackedPropertyDescriptor(
                obj => pi.GetValue(obj, null),
                (obj, val) => pi.SetValue(obj, val, null),
                isDefaultSpecifier,
                defaultValue);
        }

        private string ConstructPropertyKey(string propertyName)
        {
            return string.Format("{0}_{1}.{2}", TargetReference.Target.GetType().Name, Key, propertyName);
        }
    }
}