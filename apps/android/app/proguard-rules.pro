# kotlinx.serialization
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.**
-keepclassmembers class kotlinx.serialization.json.** { *** Companion; }
-keep,includedescriptorclasses class com.veltrix.os.**$$serializer { *; }
-keepclassmembers class com.veltrix.os.** { *** Companion; }
-keepclasseswithmembers class com.veltrix.os.** { kotlinx.serialization.KSerializer serializer(...); }
