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
    System.getenv("VELTRIX_ADMIN_KEYSTORE_PATH")?.let { setProperty("storeFile", it) }
    System.getenv("VELTRIX_ADMIN_KEYSTORE_PASSWORD")?.let { setProperty("storePassword", it) }
    System.getenv("VELTRIX_ADMIN_KEY_ALIAS")?.let { setProperty("keyAlias", it) }
    System.getenv("VELTRIX_ADMIN_KEY_PASSWORD")?.let { setProperty("keyPassword", it) }
}
val hasReleaseSigning = signingProps.size == 4

android {
    namespace = "com.veltrix.admin"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.veltrix.admin"
        minSdk = 26
        targetSdk = 36
        versionCode = buildNumber
        versionName = veltrixVersion
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        // No default server: the owner enters the server URL on first launch. Cleartext HTTP is never allowed outside debug.
        buildConfigField("boolean", "ALLOW_CLEARTEXT_DEV_HOSTS", "false")
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
            // Debug builds may talk plain HTTP to the emulator host (10.0.2.2) / localhost only.
            buildConfigField("boolean", "ALLOW_CLEARTEXT_DEV_HOSTS", "true")
            resValue("string", "build_label", "DEBUG BUILD — not for production")
        }
        // Shrunk, installable test build signed with the debug key — clearly labelled, never for production. HTTPS only.
        create("staging") {
            initWith(getByName("debug"))
            isDebuggable = false
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            applicationIdSuffix = ".test"
            versionNameSuffix = "-test"
            buildConfigField("boolean", "ALLOW_CLEARTEXT_DEV_HOSTS", "false")
            resValue("string", "build_label", "TEST BUILD — debug-signed, not for production")
            signingConfig = signingConfigs.getByName("debug")
            matchingFallbacks += listOf("release")
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
    testOptions { unitTests.isReturnDefaultValues = true }
    lint {
        abortOnError = true
        warningsAsErrors = false
        checkReleaseBuilds = true
        disable += listOf("GradleDependency", "NewerVersionAvailable", "AndroidGradlePluginVersion", "OldTargetApi")
    }
}

// Name APK outputs VELTRIX-Admin-Android-{version}[-debug|-test|-unsigned].apk
androidComponents {
    onVariants { variant ->
        variant.outputs.forEach { output ->
            val suffix = when {
                variant.buildType == "debug" -> "-debug"
                variant.buildType == "staging" -> "-test"
                !hasReleaseSigning -> "-unsigned"
                else -> ""
            }
            (output as? com.android.build.api.variant.impl.VariantOutputImpl)?.outputFileName?.set("VELTRIX-Admin-Android-$veltrixVersion$suffix.apk")
        }
    }
}

kotlin {
    compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17) }
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
    implementation(libs.compose.icons)
    debugImplementation(libs.compose.ui.tooling)
    implementation(libs.hilt.android)
    ksp(libs.hilt.compiler)
    implementation(libs.hilt.navigation.compose)
    implementation(libs.biometric)
    implementation(libs.okhttp)
    implementation(libs.serialization.json)
    implementation(libs.coroutines.android)
    testImplementation(libs.junit)
    testImplementation(libs.coroutines.test)
}
