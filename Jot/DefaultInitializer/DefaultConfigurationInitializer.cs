﻿using Jot.Triggers;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace Jot.DefaultInitializer
{
    /// <summary>
    /// Default initializer that will be used if a more specific initializer is not specified. 
    /// Enables [Trackable] and [TrackingKey] attributes, ITrackingAware and ITriggerPersist interfaces.
    /// Can be overriden to allow additional initialization logic for a specific type. If you do not wish 
    /// to keep the logic that deals with [Trackable], [TrackingKey], ITrackingAware and ITriggerPersist, 
    /// implement IConfigurationInitializer directly instead.
    /// </summary>
    public class DefaultConfigurationInitializer : IConfigurationInitializer
    {
        /// <summary>
        /// Applies to type System.Object
        /// </summary>
        public virtual Type ForType { get { return typeof(object); } }

        /// <summary>
        /// Initializes the tracking configuration for a target object
        /// </summary>
        /// <param name="configuration"></param>
        public virtual void InitializeConfiguration(TrackingConfiguration configuration)
        {
            object target = configuration.TargetReference.Target;

            //set key if [TrackingKey] detected
            Type targetType = target.GetType();
            PropertyInfo keyProperty = targetType.GetProperties().SingleOrDefault(pi => pi.IsDefined(typeof(TrackingKeyAttribute), true));
            if (keyProperty != null)
                configuration.Key = keyProperty.GetValue(target, null).ToString();

            //add properties that have [Trackable] applied
            foreach (PropertyInfo pi in targetType.GetProperties())
            {
                TrackableAttribute propTrackableAtt = pi.GetCustomAttributes(true).OfType<TrackableAttribute>().Where(ta => ta.TrackerName == configuration.StateTracker.Name).SingleOrDefault();
                if (propTrackableAtt != null)
                {
                    if (propTrackableAtt.IsDefaultSpecified)
                        configuration.AddProperty(pi.Name, propTrackableAtt.DefaultValue);
                    else
                        configuration.AddProperty(pi.Name);
                }
            }

            //allow the object to alter its configuration
            ITrackingAware trackingAwareTarget = target as ITrackingAware;
            if (trackingAwareTarget != null)
                trackingAwareTarget.InitConfiguration(configuration);
        }
    }
}
