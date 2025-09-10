import pandas as pd
import matplotlib.pyplot as plt
import matplotlib.dates as mdates
import os

BASE_DIR = r"C:\Users\Matic\Desktop\Samsung Health"
CSV_FILES = [
    "steps_summary_shealth_fixed.csv",
    "steps_summary_pedometer_fixed.csv"
]

def plot_bar_chart(df, x, y, title, xlabel, ylabel, output_path, width=50, height=10, dpi=200, freq='D', source_text=None):
    plt.figure(figsize=(width, height), dpi=dpi)
    ax = plt.gca()

    # Bar chart
    plt.bar(df[x], df[y], width=0.8, color='#1976d2', edgecolor='#0d47a1')

    plt.title(title, fontsize=36, pad=30)
    plt.xlabel(xlabel, fontsize=26, labelpad=20)
    plt.ylabel(ylabel, fontsize=26, labelpad=20)

    # Formatting x-axis
    if freq == 'D':
        ax.xaxis.set_major_locator(mdates.MonthLocator())
        ax.xaxis.set_major_formatter(mdates.DateFormatter('%b %Y'))
        ax.xaxis.set_minor_locator(mdates.WeekdayLocator(byweekday=mdates.MO))
        plt.tick_params(axis='x', which='major', labelsize=18, rotation=45, length=10)
    elif freq == 'M':
        ax.xaxis.set_major_locator(mdates.MonthLocator())
        ax.xaxis.set_major_formatter(mdates.DateFormatter('%b %Y'))
        plt.tick_params(axis='x', which='major', labelsize=24, rotation=45, length=12)

    plt.tick_params(axis='y', which='major', labelsize=20, length=10)
    plt.grid(which='major', axis='y', color='#b0bec5', linestyle='-', linewidth=1.3)
    plt.grid(which='minor', axis='y', color='#cfd8dc', linestyle=':', linewidth=0.6)

    ax.spines['top'].set_visible(False)
    ax.spines['right'].set_visible(False)

    # Add source (CSV path) to the image
    if source_text:
        # Bottom-left corner of the figure
        plt.figtext(0.01, 0.01, f"Source: {source_text}", ha='left', va='bottom', fontsize=12, color='#546e7a')

    plt.tight_layout(pad=4)
    plt.savefig(output_path, bbox_inches='tight')
    plt.close()
    print(f"Saved: {output_path}")

def process_and_plot(csv_path, prefix):
    df = pd.read_csv(csv_path)
    df['date'] = pd.to_datetime(df['date'])
    df = df.sort_values('date')

    source_text = csv_path  # include full path with .csv

    # DAILY STEPS
    plot_bar_chart(
        df,
        x='date',
        y='steps',
        title=f"Step Count Per Day ({prefix})",
        xlabel="Date",
        ylabel="Steps",
        output_path=os.path.join(BASE_DIR, f"{prefix}_steps_daily.png"),
        width=50, height=10, dpi=200, freq='D',
        source_text=source_text
    )

    # DAILY DISTANCE
    plot_bar_chart(
        df,
        x='date',
        y='distance_km',
        title=f"Distance (km) Per Day ({prefix})",
        xlabel="Date",
        ylabel="Distance (km)",
        output_path=os.path.join(BASE_DIR, f"{prefix}_km_daily.png"),
        width=50, height=10, dpi=200, freq='D',
        source_text=source_text
    )

    # MONTHLY DISTANCE
    df['month'] = df['date'].dt.to_period('M').dt.to_timestamp()
    df_month = df.groupby('month')['distance_km'].sum().reset_index()
    plot_bar_chart(
        df_month,
        x='month',
        y='distance_km',
        title=f"Distance (km) Per Month ({prefix})",
        xlabel="Month",
        ylabel="Distance (km)",
        output_path=os.path.join(BASE_DIR, f"{prefix}_km_monthly.png"),
        width=30, height=10, dpi=200, freq='M',
        source_text=source_text
    )

def main():
    file_map = {
        "steps_summary_shealth_fixed.csv": "Samsung Health (Shealth)",
        "steps_summary_pedometer_fixed.csv": "Samsung Health (Pedometer)",
    }
    for filename in CSV_FILES:
        csv_path = os.path.join(BASE_DIR, filename)
        if os.path.exists(csv_path):
            process_and_plot(csv_path, os.path.splitext(filename)[0])
        else:
            print(f"File not found: {csv_path}")

if __name__ == "__main__":
    main()
