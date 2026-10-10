# kotlinx.serialization
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.**
-keepclassmembers class kotlinx.serialization.json.** { *** Companion; }
-keep,includedescriptorclasses class com.veltrix.admin.**$$serializer { *; }
-keepclassmembers class com.veltrix.admin.** { *** Companion; }
-keepclasseswithmembers class com.veltrix.admin.** { kotlinx.serialization.KSerializer serializer(...); }
