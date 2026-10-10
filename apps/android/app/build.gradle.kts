import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.ksp)
    alias(libs.plugins.hilt)
}

// Version comes from CI (VELTRIX_VERSION / VELTRIX_BUILD_NUMBER) so APK names and versionCode are traceable.
val veltrixVersion: String = System.getenv("VELTRIX_VERSION") ?: "0.1.0"
val buildNumber: Int = (System.getenv("VELTRIX_BUILD_NUMBER") ?: "1").toInt()

// Release signing is configured only from environment/CI secrets — keystores and passwords are never committed.
val signingProps = Properties().apply {
    System.getenv("VELTRIX_KEYSTORE_PATH")?.let { setProperty("storeFile", it) }
    System.getenv("VELTRIX_KEYSTORE_PASSWORD")?.let { setProperty("storePassword", it) }
    System.getenv("VELTRIX_KEY_ALIAS")?.let { setProperty("keyAlias", it) }
    System.getenv("VELTRIX_KEY_PASSWORD")?.let { setProperty("keyPassword", it) }
}
val hasReleaseSigning = signingProps.size == 4

android {
    namespace = "com.veltrix.os"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.veltrix.os"
        minSdk = 26
        targetSdk = 36
        versionCode = buildNumber
        versionName = veltrixVersion
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        // Optional pinned vendor licensing public key (SPKI, base64). When empty the key is pinned on first activation.
        buildConfigField("String", "VENDOR_PUBLIC_KEY", "\"${System.getenv("VELTRIX_VENDOR_PUBLIC_KEY") ?: ""}\"")
        buildConfigField("String", "DEFAULT_SERVER_URL", "\"${System.getenv("VELTRIX_DEFAULT_SERVER") ?: "https://api.veltrix.example"}\"")
        resourceConfigurations += listOf("en", "fr", "ar")
    }

    signingConfigs {
        if (hasReleaseSigning) {
            create("release") {
                storeFile = file(signingProps.getProperty("storeFile"))
                storePassword = signingProps.getProperty("storePassword")
                keyAlias = signingProps.getProperty("keyAlias")
                keyPassword = signingProps.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        debug {
            applicationIdSuffix = ".debug"
            versionNameSuffix = "-debug"
            resValue("string", "build_label", "DEBUG BUILD — not for production")
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            resValue("string", "build_label", if (hasReleaseSigning) "" else "UNSIGNED RELEASE BUILD")
            if (hasReleaseSigning) signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    buildFeatures {
        compose = true
        buildConfig = true
    }
    packaging { resources.excludes += "/META-INF/{AL2.0,LGPL2.1}" }
    lint {
        abortOnError = true
        warningsAsErrors = false
        checkReleaseBuilds = true
        disable += listOf("GradleDependency", "NewerVersionAvailable", "AndroidGradlePluginVersion", "OldTargetApi")
    }
}

// Name APK outputs VELTRIX-OS-Android-{version}[-debug|-unsigned].apk
androidComponents {
    onVariants { variant ->
        variant.outputs.forEach { output ->
            val suffix = when {
                variant.buildType == "debug" -> "-debug"
                !hasReleaseSigning -> "-unsigned"
                else -> ""
            }
            (output as? com.android.build.api.variant.impl.VariantOutputImpl)?.outputFileName?.set("VELTRIX-OS-Android-$veltrixVersion$suffix.apk")
        }
    }
}

kotlin {
    compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17) }
}

ksp {
    arg("room.schemaLocation", "$projectDir/schemas")
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.core.splashscreen)
    implementation(libs.androidx.appcompat)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.navigation.compose)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.graphics)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.compose.material3)
    implementation(libs.compose.material3.adaptive)
    implementation(libs.compose.icons)
    debugImplementation(libs.compose.ui.tooling)
    implementation(libs.hilt.android)
    ksp(libs.hilt.compiler)
    implementation(libs.hilt.navigation.compose)
    implementation(libs.hilt.work)
    ksp(libs.hilt.work.compiler)
    implementation(libs.room.runtime)
    implementation(libs.room.ktx)
    ksp(libs.room.compiler)
    implementation(libs.work.runtime)
    implementation(libs.biometric)
    implementation(libs.camerax.camera2)
    implementation(libs.camerax.lifecycle)
    implementation(libs.camerax.view)
    implementation(libs.mlkit.barcode)
    implementation(libs.datastore)
    implementation(libs.okhttp)
    implementation(libs.serialization.json)
    implementation(libs.coroutines.android)
    testImplementation(libs.junit)
    testImplementation(libs.coroutines.test)
}
